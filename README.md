# AI Learning — Azure OpenAI / RAG Spike

A learning spike in C# for building LLM applications on Azure: retrieval-augmented
generation over a document, and function calling so the model can invoke C# methods.
Written to be understood rather than reused — every mechanism is hand-rolled against the
raw Azure SDKs, with no orchestration framework.

The working program is [`LlmSpike/Program.cs`](LlmSpike/Program.cs) — a single file, ~430 lines,
including the eval suite it runs against itself.

---

## What broke, and why

Three failures were produced deliberately and confirmed by reverting a single variable.
They are the point of the spike; the working code is the easy part.

**1. Bad chunk boundaries break answers that grounding instructions cannot save.**
Splitting the document on single newlines instead of paragraph breaks severed a paragraph
mid-thought. Asked for a VAT-inclusive total, the model returned a confident, well-formatted
**17,598.05** against a correct **14,913.60** — showing its working. It had obeyed the
instruction to use only the supplied context. The context arrived damaged. Restoring
paragraph splitting restored the right answer, with nothing else changed.

**2. Model arithmetic is unreliable, and the fix is a function, not a better prompt.**
Giving the model a C# method to call moved the calculation out of the language model
entirely.

**3. A function fixes calculation, but not extraction.** Re-breaking the chunking later fed
`14,793.60` — a number that appears nowhere in the document, apparently blended from
`11,793.60` and `14,913.60` — into a correct penalty calculation. The arithmetic was
flawless; the argument was corrupt, so the answer was a perfectly computed **443.81**
instead of **447.41**. Garbage in, correctly-computed garbage out, and harder to spot than
an arithmetic error because the output is internally consistent.

That third one was caught by an eval asserting the *right* answer. The guard against the
first bug's wrong number never fired, because the same root cause surfaced a different one —
wrong answers are unbounded, so you cannot enumerate them.

Full write-ups in [`notes/`](notes/).

---

## How to run it

Requires .NET 10 SDK and an Azure subscription with the resources below.

```bash
cd LlmSpike
dotnet run
```

Secrets are stored via `dotnet user-secrets`, outside the repo. If they're missing
(fresh machine, or `AOAI_ENDPOINT` comes back null), set them:

```bash
cd LlmSpike
dotnet user-secrets set "AOAI_ENDPOINT"   "https://<resource>.openai.azure.com/"
dotnet user-secrets set "AOAI_KEY"        "<azure openai key>"
dotnet user-secrets set "SEARCH_ENDPOINT" "https://<service>.search.windows.net"
dotnet user-secrets set "SEARCH_KEY"      "<search admin key>"
```

Check what's already set with `dotnet user-secrets list`.

**Each run drops and rebuilds the search index, re-embeds the document, and then runs the
full eval suite** — roughly 20 API calls and 30–60 seconds. Negligible money, but it isn't
instant like a unit test.

### Azure resources this expects

| | |
|---|---|
| Resource group | `ai-learning`, East US |
| Azure OpenAI | `<your-openai-resource>`, Standard S0 |
| — chat deployment | `chat` → gpt-4.1-mini |
| — embeddings deployment | `embeddings` → text-embedding-3-small (1536 dims) |
| Azure AI Search | Free tier, index `customs-chunks` |

Deployment names are referenced in code; the underlying models can be swapped in the
portal without touching C#. The OpenAI key belongs to the *resource* and covers both
deployments.

---

## What the program does

A hardcoded customs declaration is indexed, then seven eval cases are run against it:

1. Drop and rebuild an Azure AI Search index (HNSW, 1536-dim vector field).
2. Split the document on paragraph breaks, embed each chunk, upload.
3. Describe three tools to the model: `search_documents`, `calculate_late_penalty`,
   `convert_currency`.
4. For each eval case, run a dispatch loop: call the model → if it requests tools, run them
   and append the results → call again → repeat until it returns text.
5. Assert on the answer, print PASS/FAIL per case, exit non-zero if any failed.

The index is dropped rather than updated because documents are keyed by position, so
re-indexing a smaller set would silently orphan the surplus rows. That bug is written up in
[`notes/session4-evals.md`](notes/session4-evals.md) — a suite that passed 7/7 against an
index holding nine stale chunks.

Retrieval is one of the tools, not a fixed pipeline step, so the model decides whether to
search, writes its own search query, and can search more than once. That choice is the
most important thing in the file.

---

## What's covered

- Stateless chat calls, message arrays, system prompts
- Embeddings, vector dimensions, cosine similarity written by hand
- Chunking, and a confirmed failure caused by bad chunk boundaries
- Fixed-k vs threshold retrieval, and why thresholds don't transfer between implementations
- Azure AI Search, HNSW, vector vs keyword vs hybrid
- Function calling: the dispatch loop, tool descriptions, tool error handling
- Multi-step chaining — search feeding a calculation, unprompted
- Prompt-level scope control
- An eval suite: seven cases, pass/fail per case, non-zero exit on failure

---

## What's next

**The learning list is done.** Evals were the last item.

**Now — the real project.** A customs document processing tool, in its own repo:

> Upload → Document Intelligence extracts text and fields → chunk and embed → store
> vectors in Azure AI Search → natural-language Q&A over the document → answers with
> citations → review queue for low-confidence extractions.

ASP.NET Core API, .NET 10, C#. A normal web application calling three Azure services.
This spike is the reference, not the starting point.

**Deliberately skipped — Semantic Kernel.** A keyword rather than a necessity; plenty of
production .NET LLM work runs against the raw SDK. Adding an abstraction over mechanics
just learned by hand is a net loss.

---

## Repo layout

```
README.md                  you are here
LlmSpike/
  Program.cs               the current program: RAG + tools + evals
  Program.Search.txt       frozen snapshot — session 2, retrieval only
  Program.Tools.txt        frozen snapshot — session 3, tools only
notes/
  session1-rag-fundamentals.md
  session2-azure-ai-search.md
  session3-function-calling.md
  session3-part2-rag-plus-tools.md
  session4-evals.md
  working-notes.md         how to pick the project back up
```

The two `.txt` snapshots are kept deliberately: they show the two halves separately, before
the merge made them harder to read in isolation.
