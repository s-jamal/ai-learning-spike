# LLM Spike — Session Notes

Learning log for the Azure OpenAI / RAG spike. C# throughout, no Python.

---

## 1. Provisioning (the part that eats the first evening)

**Structure built:**

- Subscription: `Personal`
- Resource group: `ai-learning`
- Resource: `<your-openai-resource>` (Azure OpenAI, East US, Standard S0)
- Deployments: `chat` → gpt-4.1-mini, `embeddings` → text-embedding-3-small

**Choices and why:**

| Decision | Reasoning |
|---|---|
| Plain Azure OpenAI resource, not Foundry | Foundry adds projects, hubs, connections. Three extra concepts before the first API call. Can be added later without redoing anything. |
| Public network access | Console app calling from a laptop. Private endpoint would need a VNet first. |
| Deployment named `chat`, not `gpt-4.1-mini` | Code references the deployment name. Model swaps and deprecations then require zero code changes. |
| Global Standard deployment type | Pay per token, routed wherever Azure has capacity. Cheapest and most available. |

**Deployment types worth knowing:**

- **Global Standard** — pay per call, global routing. Default choice for dev.
- **Data Zone Standard** — processing pinned to a geography. Matters for EU data residency.
- **Provisioned (PTU)** — reserved capacity billed hourly whether used or not. Enterprise.
- **Batch** — async bulk jobs via file upload. Cannot serve interactive calls.

### Quota — the actual blocker

Quota is allocated **per subscription, per region, per model, per deployment type**. A new subscription can show zero across current-generation models.

Reading the quota table:

- `0 of 200K TPM` → cap exists, none assigned yet. **Deploy immediately, no request needed.**
- `0` with no denominator → no capacity. Must request.

That distinction cost an evening. Everything looked like zero until the rows were expanded individually.

Requesting quota:

- Form is written for enterprises but individuals use it routinely
- Enter kTPM, not raw TPM (`30` = 30,000 TPM). Typing `30000` reads as 30M and gets declined
- Justification should be specific and small: real project, single user, low throughput
- Denials are often regional capacity, not a judgement on the request
- **30-day cooldown is per region** — a denial for East US doesn't block resubmitting for Sweden Central
- Requests are prioritised for subscriptions already consuming existing quota, so deploy something first

**Production note for later:** public network access plus key auth is fine for a spike. Production would use a private endpoint and Entra ID managed identity instead of keys.

---

## 2. Core concepts

### An LLM call is a stateless HTTP request

Text in, text out. No session, no memory. To make the model aware of earlier turns, resend them. The context window is just the size limit on one request.

Proven directly:

1. "My name is Jamal." → acknowledged
2. New call: "What is my name?" → doesn't know
3. Same question with the prior exchange replayed as messages → knows

`AssistantChatMessage` is how you tell the model "you said this." It will believe whatever text you put there.

**Consequence:** long conversations mean resending everything each turn. Token cost grows quadratically and eventually hits the window limit. Fixes range from keeping the last N turns (cheap) to summarising older ones (more work). Single-turn Q&A over documents sidesteps this entirely.

### Embeddings

An embedding model turns text into a fixed-length array of floats. `text-embedding-3-small` → 1536 dimensions. `text-embedding-3-large` → 3072.

The useful property: text with similar **meaning** produces numerically similar arrays, regardless of shared words.

### Cosine similarity

Measures the angle between two vectors, ignoring their magnitude. 1.0 = same direction, ~0 = unrelated. In practice with these embeddings, roughly 0.0 to 0.9 — a ranking number, not an absolute score.

Angle rather than distance because vector magnitude tracks things like text length, which is irrelevant. Direction carries the meaning.

```csharp
static float Cosine(float[] a, float[] b)
{
    float dot = 0, magA = 0, magB = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        magA += a[i] * a[i];
        magB += b[i] * b[i];
    }
    return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
}
```

### Chunking

Splitting a document into indexable pieces. Whatever chunk gets retrieved is what lands in the prompt.

- Too large → wasted tokens, diluted signal
- Too small → sentences cut mid-thought, relationships between facts lost

Most bad RAG systems fail here, not at the model.

### RAG (Retrieval-Augmented Generation)

The model has never seen your data. Putting the whole corpus in every prompt doesn't fit and doesn't scale. So:

1. Embed all chunks once → the index
2. Embed the incoming question
3. Cosine similarity → find the closest chunks
4. Paste them into a prompt with "answer using only this context"
5. One chat call

Steps 1–3 retrieval, 4 augmentation, 5 generation.

Underneath it is string concatenation plus one HTTP request. The prompt that goes out reads:

```
Context:
- Import duty is assessed at 5 percent of customs value, giving 3,120 USD...

Question: How much duty is owed on this shipment?
```

That's the whole mechanism. No hidden machinery.

**Why it beats fine-tuning for this:** cheap, no training, updates instantly when a document changes.

---

## 3. Retrieval strategy

### Fixed top-k vs threshold

**Fixed k** — always take the N best. Simple, but on an off-topic query it hands the model N irrelevant chunks anyway.

**Threshold** — take everything scoring above a bar (0.35 worked here), optionally capped. An off-topic query returns empty context and the model correctly declines.

Typical starting point for real content: 3–5 chunks, tuned by whether answers come back incomplete (raise k) or muddled (lower it).

### Diagnosing empty answers

When RAG returns "I don't know", two very different causes:

- **Low scores** → retrieval missed. Fix chunking, embedding, or query phrasing.
- **High scores but no answer** → the content genuinely isn't in the document.

Printing the scores alongside the answer is what makes this distinguishable. Worth keeping in any real implementation.

---

## 4. Experiments and results

### Grounding holds against outside knowledge

Query: "What is the capital of France?" against a customs document. All scores under 0.04, nothing passed threshold, model declined to answer despite obviously knowing. The system prompt held.

### Retrieving titles vs retrieving content

Five one-line document titles. Query "How much duty do I owe on this shipment?" correctly ranked *Tax breakdown for imported goods* (0.4874) above *Customs declaration* (0.4740), and both far above *The cat sat on the mat* (0.0334).

Note the top hit shares **zero words** with the query. Keyword search scores that at nothing. This is the capability vector search adds.

But the answer was still "I don't know" — because a title contains no figures. Retrieval was correct; the content wasn't there.

### Chunking breakage produced a confident hallucination

Splitting on single newlines instead of paragraph breaks cut this paragraph apart:

> Import duty is assessed at 5 percent of customs value, giving 3,120 USD. Value added tax is applied at 18 percent on the sum of customs value and duty, giving 11,793.60 USD. Total payable to customs is 14,913.60 USD.

Query: "What is the total payable including VAT?"

Correct answer: **14,913.60 USD** (VAT already included).

Model returned: 14,913.60 × 0.18 = 2,684.45, total **17,598.05 USD**. Confident, formatted, showed its working, wrong.

**Why this matters:** the grounding instruction was followed. The model used only the supplied context. It still produced a wrong number because the context arrived damaged.

**Confirmed by reverting.** Restoring paragraph splitting — `{ "\r\n\r\n", "\n\n" }` in place of `{ "\r\n", "\n" }` — returned the correct 14,913.60 USD. Same model, same prompt, same query, chunk boundaries the only variable.

Two separate failure modes:

- Model inventing facts → fixed by grounding instructions
- Model misreading broken context → **not** fixed by grounding instructions

### Why evals follow from this

The error was caught only because the right answer was known. Across a hundred documents it wouldn't be. The systematic version is a set of question/expected-answer pairs run after every change to chunking, retrieval, or prompt — so a chunking tweak that breaks arithmetic surfaces as a failing test, not as a wrong invoice weeks later.

---

## 5. Code reference

**Setup:**

```
dotnet new console -n LlmSpike
cd LlmSpike
dotnet add package Azure.AI.OpenAI
dotnet add package Microsoft.Extensions.Configuration
dotnet add package Microsoft.Extensions.Configuration.UserSecrets

dotnet user-secrets init
dotnet user-secrets set "AOAI_ENDPOINT" "https://<resource>.openai.azure.com/"
dotnet user-secrets set "AOAI_KEY" "<key>"
```

The key belongs to the **resource**, not the deployment. One key covers both `chat` and `embeddings`.

**Client:**

```csharp
var client = new AzureOpenAIClient(
    new Uri(config["AOAI_ENDPOINT"]!),
    new AzureKeyCredential(config["AOAI_KEY"]!));

var chat = client.GetChatClient("chat");          // deployment name
var embed = client.GetEmbeddingClient("embeddings");
```

**Chat with a system message:**

```csharp
var response = await chat.CompleteChatAsync(
    new SystemChatMessage("Answer concisely."),
    new UserChatMessage("What is an HS code?"));

Console.WriteLine(response.Value.Content[0].Text);
```

**Batch embeddings** — one round trip, order preserved:

```csharp
var results = await embed.GenerateEmbeddingsAsync(docs);

var index = new List<(string Text, float[] Vector)>();
for (int i = 0; i < docs.Count; i++)
{
    index.Add((docs[i], results.Value[i].ToFloats().ToArray()));
}
```

Real indexes need batching in groups (~100) with retry on rate limits, since there are caps on inputs and tokens per call.

Keep text and vector together in one structure rather than two parallel lists — nothing enforces alignment otherwise, and `Zip` silently truncates to the shorter sequence.

**Separator ordering in `Split`:** separators are matched in array order, so the longer form goes first. `{ "\r\n\r\n", "\n\n" }` for paragraphs, `{ "\r\n", "\n" }` for lines. Reversed, Windows line endings match the shorter form first and leave stray `\r` characters at chunk boundaries.

---

## 6. Next steps

**In the spike:**

- Azure AI Search — replaces the in-memory scan. Same cosine similarity, made fast via approximate nearest neighbour indexing. Full scan over every vector doesn't survive 50k chunks.
- Then the spike is done and gets deleted.

**Real project — customs document processing tool:**

Upload → Document Intelligence extracts text and fields → chunk and embed → store vectors in Azure AI Search → natural-language Q&A over the document → answers with citations → review queue for low-confidence extractions.

ASP.NET Core API, .NET 8, C#. A normal web application that calls three Azure services.



**Remaining learning sequence:**

Azure AI Search → Semantic Kernel orchestration → function calling → evals and guardrails

---

## 7. Talking points



The keyword-vs-vector contrast is worth keeping as a talking point: a Solr keyword index can't match "how much duty do I owe" against a paragraph about tax breakdowns, because there are no shared terms. Understanding why that happens is the part worth retaining.

**Interview material from this session:**

- Deliberately broke chunking and watched the model produce a confidently wrong VAT calculation — concrete evidence for why evals matter
- Grounding instructions prevent invention but not misreading of damaged context
- Threshold-based retrieval vs fixed top-k, and the tradeoff
- Deployment naming as a decoupling strategy against model deprecation
- Global Standard for dev, Data Zone Standard for EU residency, Provisioned for guaranteed throughput
