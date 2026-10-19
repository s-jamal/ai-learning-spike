# LLM Spike — Session 3, Part 2: Merging RAG and Tools

Combining retrieval and function calling into one program. Continues from `session3-function-calling.md`.

---

## 1. The architectural change

Everything else in the merge was copy-paste from sessions 2 and 3. One thing changed, and it is the whole point of this session.

### Before: retrieval was a hardcoded pipeline step

```csharp
async Task Answer(string query)
{
    var chunks = await Search(query);          // always runs
    var response = await chat.CompleteChatAsync(
        systemMessage,
        new UserChatMessage($"Context:\n{chunks}\nQuestion: {query}"));
}
```

Application code decided to search. Every question, every time, using the user's exact words as the search query.

### After: retrieval is a tool the model can choose

```csharp
var searchTool = ChatTool.CreateFunctionTool(
    functionName: "search_documents",
    functionDescription: "Searches the customs declaration documents. Use this to find "
                       + "any factual detail about a declaration: amounts, dates, HS codes, "
                       + "importer names, payment terms, penalty rates.",
    ...);
```

`SearchDocuments` itself is unchanged — embed the query, run vector + keyword search, return the chunks as a string. It contains no decision logic. It is a plain method.

**Making it a tool moves the decision out of application code and into the model.**

Three consequences:

1. **It can be skipped.** Off-topic questions produce no search — no embedding call, no search request.
2. **The model writes its own query.** It reformulates rather than passing the user's words through.
3. **It can search more than once.** If the first result is insufficient, the loop keeps going and it can call again with different terms.

That is the line between a fixed RAG pipeline and something people call agentic. It is about forty lines of C#.

**Note:** tools must return `string`. Everything going back to the model is text.

---

## 2. The system prompt now does three jobs

```
"You are a customs assistant. Use search_documents to find facts in the
declaration before answering. Use the calculation tools rather than doing
arithmetic yourself. Only answer questions about customs declarations and
payments; decline anything else."
```

- Push toward retrieval instead of guessing
- Use tools for arithmetic — directly addresses the session 1 VAT failure
- Scope control

---

## 3. Observed behaviour

### Multi-step chaining, unprompted

Question: *"What is the penalty if I pay this declaration 30 days late?"*

1. `search_documents({"query": "outstanding amount and penalty rate"})` — **not the user's wording**; the model reformulated the question into what it needed to look up
2. Retrieved the payment terms chunk; read 14,913.60 and the 0.1%/day rate out of it
3. `calculate_late_penalty({"amount": 14913.6, "daysLate": 30})` — amount from the *search result*, days from the *user's question*
4. Answer: 447.41 USD ✓

Two loop iterations, with arguments assembled from two different sources. Nothing in the code specified that sequence. Three tools were described; the model worked out the path.

### Same pattern, different pairing

*"What is the total payable, in euros?"* → search for the total → `convert_currency(14913.6, EUR)` → 13,720.51 EUR.

Only one conversion call this run. In session 3 part 1, an equivalent question produced a wasted speculative call. **Same code, different run, different tool sequence.** Model behaviour is non-deterministic — worth registering rather than assuming a pattern from one run.

### Scope control held

*"What is the capital of France?"* → zero tool calls, declined. The model correctly deciding not to act.

---

## 4. Honest limitation

This worked cleanly because the task is small and the three tool descriptions are unambiguous. With ten tools, overlapping descriptions, or vaguer questions, the model picks the wrong tool, loops, or calls things in a useless order.

The real engineering problem in agentic systems is not making it work once — it is making it reliable across many inputs. That is what evals are for, and it remains the last item on the learning list.

---

## 5. How to work with generated code

Reading and running code someone else wrote feels like learning but is mostly recognition, not recall.

Better pattern:

1. **Predict first** — write down which tools will fire, in what order, with what arguments, before running
2. **Run and compare** — where the prediction was wrong is the actual lesson
3. **Debug only the surprising part** — breakpoint on the `foreach` over `ToolCalls`, inspect `messages` on the second iteration

Going forward: **ask for the shape, not the finished code.** "How do I make retrieval a tool?" gets the mechanism and the API names; writing it produces compiler errors, and fixing those is what sticks.

### What actually needs to be known

Not SDK syntax. Nobody writes `SearchIndexClient` constructors from memory.

- **The shape of the loop** — call model, if it wants tools run them and append results, call again, repeat until text. Should be drawable on a whiteboard.
- **Which component decides what** — model requests, code executes; retrieval finds context; the prompt constrains scope; the tool description drives selection.
- **Failure modes and causes** — bad chunking broke a VAT calculation; model arithmetic is unreliable so hand it a function; thresholds don't port between retrieval implementations; tool errors return as strings rather than throwing.
- **Decisions and their alternatives** — retrieval as a tool vs a fixed pipeline step; fixed k vs threshold; vector vs hybrid.

The working test: **can you walk someone through the file and justify the structure without reading the comments?**

This matters most for the real project. A repo written entirely by an assistant and approved as diffs is a repo that can't be defended when someone asks why retrieval is a tool rather than a pipeline step.

---

## 6. Spike complete

Full pattern covered: retrieval, tool use, multi-step chaining, scope control, error handling.

**Time spent:** roughly 6 hours across three sessions.

**Next:** leave the spike behind and start the customs document project. Semantic Kernel and evals/guardrails remain on the learning list — Semantic Kernel is optional, a keyword rather than a necessity, since plenty of production .NET LLM work runs against the raw SDK.
