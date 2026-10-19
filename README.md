# AI Learning — Azure OpenAI / RAG Spike

A learning spike in C# for building LLM applications on Azure: retrieval-augmented
generation over a document, and function calling so the model can invoke C# methods.
Written to be understood rather than reused — every mechanism is hand-rolled against the
raw Azure SDKs, with no orchestration framework.

The working program is [`LlmSpike/Program.cs`](LlmSpike/Program.cs) — a single file, ~430 lines,
including the eval suite it runs against itself.

---

## Start here each session

1. Read this file (2 minutes).
2. `cd LlmSpike && dotnet run` to confirm it still works.
3. Pick up at **What's next** below.

Detailed reasoning, experiments and failure findings live in [`notes/`](notes/) — read those
only when you need the *why* behind a specific decision, not to re-orient.

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
`notes/session4-evals.md`.

Retrieval is one of the tools, not a fixed pipeline step, so the model decides whether to
search, writes its own search query, and can search more than once. That choice is the
most important thing in the file.

---

## What's done

Roughly 6 hours across three sessions. Covered and verified working:

- Stateless chat calls, message arrays, system prompts
- Embeddings, vector dimensions, cosine similarity written by hand
- Chunking, and a confirmed failure caused by bad chunk boundaries
- Fixed-k vs threshold retrieval, and why thresholds don't transfer between implementations
- Azure AI Search, HNSW, vector vs keyword vs hybrid
- Function calling: the dispatch loop, tool descriptions, tool error handling
- Multi-step chaining — search feeding a calculation, unprompted
- Prompt-level scope control
- An eval suite: seven cases, pass/fail per case, non-zero exit on failure

Three confirmed failure findings, all written up in the notes and all worth being able to
tell from memory:

1. **Line-split instead of paragraph-split chunking** severed a paragraph mid-thought and
   the model returned a confidently wrong VAT total (17,598.05 against a correct
   14,913.60). Reverting the split fixed it — one variable, opposite outcome.
   Grounding instructions prevent invention but not misreading of damaged context.
2. **Model arithmetic is unreliable.** The fix is a function, not a better prompt.
3. **A function fixes calculation, not extraction.** Re-breaking the chunking fed
   `14,793.60` — a number found nowhere in the document — into a correct penalty
   calculation, producing a flawlessly computed 443.81 instead of 447.41. The eval caught
   it by asserting the *right* answer; the `MustNotContain` guard against session 1's
   17,598.05 never fired, because the same root cause produced a different wrong number.

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
just learned by hand is a net loss at a few hours a week.

---

## Working notes

- Time budget is 1–2 hours a week, so context loss between sessions is the dominant cost.
  That's what this file is for. Keep it current; it's cheaper than re-reading `notes/`.
- The spike went well partly because the code was typed and read before being run. Prefer
  asking for the *shape* of a mechanism over a finished implementation — compiler errors
  and fixing them is what sticks.
- The working test for whether a session actually landed: **can you walk someone through
  the file and justify the structure without reading the comments?**

## Repo layout

```
README.md                  you are here
LlmSpike/
  Program.cs               the current program: RAG + tools merged
  Program.Search.txt       frozen snapshot — session 2, retrieval only
  Program.Tools.txt        frozen snapshot — session 3, tools only
notes/
  session1-rag-fundamentals.md
  session2-azure-ai-search.md
  session3-function-calling.md
  session3-part2-rag-plus-tools.md
  session4-evals.md
```

The two `.txt` snapshots are kept deliberately: they show the two halves separately, before
the merge made them harder to read in isolation.
