# LLM Spike — Session 4: Evals

Turning the spike's behaviour into a set of assertions that run on every change.
Continues from `session3-part2-rag-plus-tools.md`.

---

## 1. Why this one is last and not optional

Sessions 1 and 3 each produced a confirmed failure. Both were caught **only because the
right answer happened to be known in advance**. Across a hundred documents that stops being
true, and a wrong number ships.

An eval is the systematic version of what was already being done by hand: ask a question,
compare against a known answer, do it after every change to chunking, retrieval or prompt.

Nothing about this is AI-specific. It is a test suite whose subject happens to be
non-deterministic.

---

## 2. The assertion problem

A unit test compares values. An eval compares **free text produced by a non-deterministic
system**. The same question across runs gives:

> The total payable on this customs declaration is 14,913.60 USD.
> The total payable amount is approximately 13,720.51 EUR.
> Total: $14,913.60.

Exact match tests the model's prose style, not its correctness. Useless.

### What was chosen

Every answer in this corpus reduces to a number or a proper noun, so: **normalise away
formatting, then substring match on the critical fact.** Deterministic, free, no second
model call.

```csharp
static string Normalize(string text) =>
    text.Replace(",", "")
        .Replace(" ", "")
        .Replace("$", "")
        .ToLowerInvariant();
```

`"14,913.60 USD"`, `"$14,913.60"` and `"14913.60"` all collapse to the same string.

### Two lists, not one

```csharp
record EvalCase(string Question, string[] MustContain, string[] MustNotContain);
```

`MustNotContain` exists because an answer can cite the correct figure and still conclude the
wrong one. Session 1's bug produced *"17,598.05"*; an answer reading *"17,598.05, derived
from 14,913.60 × 1.18"* would pass a naive `Contains("14913.60")` while being wrong.

Expectations are normalised too, so cases can be written readably as `"Nordwind Logistics"`
rather than `"nordwindlogistics"`. A harness that depends on remembering a formatting rule
is a harness that lies to you in three months.

---

## 3. The cases

Seven, in three groups. The grouping is the diagnostic value — "3 of 7 failed" says little,
"all four retrieval cases failed, tools fine" points straight at chunking.

| Group | Cases | Tests | If it fails |
|---|---|---|---|
| Retrieval only | total payable, import duty, HS code, importer | The fact is stated verbatim in the document | Chunking, embedding or search |
| Retrieval + tool | 30-day penalty, total in euros | `447.41` and `13720.51` exist nowhere in the document — they only appear if a C# method ran | Tool descriptions or dispatch |
| Scope control | capital of France | Model declines something it plainly knows | The system prompt stopped holding |

---

## 4. The runner

Four things worth keeping.

**Collect reasons, not a boolean.** The obvious version is `bool passed`, which tells you a
case failed but not why. Each unmet expectation appends a string instead, so failures print
`FAIL — missing "447.41"`. That line is what names the bug.

**Normalise both sides** — see above.

**A throwing case is a failed case, not a dead run.** `try/catch` per case. Without it one
malformed question kills the process and costs the other six results. Same principle as tool
errors in session 3.

**Exit non-zero on failure.**

```csharp
return failures.Count == 0 ? 0 : 1;
```

In top-level statements that becomes the process exit code. Nothing consumes it yet; it is
what lets this become a CI step without rework. It can sit above `Ask` because local
function declarations are hoisted.

`Ask` had to change from `Task` to `Task<string>` first — it previously only printed. You
cannot assert on something you only printed. The compiler walked to the single `return;`
that needed updating, and the four existing call sites still compiled untouched, because
awaiting a `Task<string>` and discarding the result is legal and does not even warn.

---

## 5. Baseline: 7/7

All seven pass, exit code 0. Three observations from the run.

**The model reformulates every query.** *"How much import duty is assessed?"* →
`search_documents({"query":"import duty amount"})`. *"Who is the importer of record?"* →
`"importer of record"`. The user's words are never passed straight through. That only
happens because retrieval is a tool rather than a pipeline step.

**Chaining works, with arguments from two sources.** The penalty case searched
`"penalty rate"`, then called `calculate_late_penalty({"amount":14913.60,"daysLate":30})` —
the amount from a *search result*, the days from the *user's question*.

**No speculative call this run.** The euros question made exactly one `convert_currency`
call. Session 3 part 1 saw the same class of question produce a wasted parallel call. Same
code, different run. More evidence for why a single observation is not a pattern — and the
reason to have evals at all.

---

## 6. Breaking it on purpose

Reverted the chunk split from `{ "\r\n\r\n", "\n\n" }` to `{ "\r\n", "\n" }` — the session 1
bug — and reran. 5 chunks became 14.

```
=== 6/7 passed ===

  FAILED: What is the penalty if I pay this declaration 30 days late?
       missing "447.41"
       got: The penalty for paying the declaration 30 days late would be 443.81 USD.
```

**It failed on a different case than expected, with a different wrong number.**

Case 1 passed this time — the fragment `giving 11,793.60 USD. Total payable to customs is
14,913.60 USD.` survived intact, so the total was still readable. Session 1's 17,598.05
never reappeared, and `MustNotContain: ["17598.05"]` never fired.

The failure landed on the penalty case:

```
[tool: calculate_late_penalty {"amount":14793.60,"daysLate":30}]
[returned: 443.81 USD]
```

**14,793.60 appears nowhere in the document.** The real figures are 11,793.60 (VAT) and
14,913.60 (total). It looks like the two blended — `14,` from one, `793.60` from the other —
after line-splitting orphaned the VAT sentence from its amount. Then
14,793.60 × 0.001 × 30 = 443.81.

### The finding

**The arithmetic was perfect. The argument was corrupted.**

`CalculateLatePenalty` did exactly its job on exactly what it was handed. This sharpens the
session 3 conclusion, which was true but incomplete:

- Session 3: *model arithmetic is unreliable, so give it a function*
- Session 4: *a function fixes calculation, not extraction*

Garbage in, correctly-computed garbage out. Worse than a visible arithmetic error, because
the output is internally consistent, correctly formatted and defensible-looking. Nothing
downstream can tell 443.81 from 447.41.

### The lesson about the assertions themselves

`MustNotContain` did not catch this. `MustContain: ["447.41"]` did.

**Wrong answers are unbounded — you cannot enumerate them.** Asserting the *right* answer is
the real test. Asserting against a known wrong one only catches an exact repeat, and the
same root cause produced a different symptom on its second outing.

That is the whole argument for evals in one line: same bug, same single variable, different
victim, different number. Prediction fails; assertion holds.

---

## 7. Bonus bug: the index was never cleared

Reverting the split and rerunning gave 7/7 — and it was wrong.

The retrieved chunk read `- giving 11,793.60 USD. Total payable to customs is 14,913.60
USD.` That is a **line fragment**, which should not exist after reverting to paragraph
splitting. Counted the index: **14 documents where 5 were expected.**

Cause:

```csharp
["id"] = i.ToString()
```

Documents are keyed by position and `Upload` is insert-or-replace. Indexing 5 chunks over a
previous 14 overwrites ids `0`–`4` and **silently orphans `5`–`13`**. Nine stale fragments
stayed queryable, and the "clean" control run scored against content no longer in the
document.

Fix — drop the index before rebuilding:

```csharp
try
{
    await indexClient.DeleteIndexAsync(indexName, CancellationToken.None);
}
catch (RequestFailedException ex) when (ex.Status == 404)
{
    // First run on a fresh service — nothing to delete.
}
```

`DeleteIndexAsync(indexName)` alone does not compile — two overloads with optional
parameters make it ambiguous. Pass `CancellationToken.None` explicitly.

Acceptable here because the whole corpus is rebuilt every run. **A real system cannot drop a
live index.** It keys documents by content hash so re-indexing overwrites the right rows, or
builds into a new index and swaps an alias across.

Clean control afterwards: 5 documents, 7/7, exit 0. Single variable confirmed.

**Worth registering:** a passing test suite was hiding a data bug. The evals did not catch
this one — reading the retrieval output did. Green is not proof.

---

## 8. Honest limitations

**This is output eval, not trajectory eval.** Assertions are on the final answer only. The
session 3 finding — a wasted speculative `convert_currency` call — would pass every one of
these cases, because the final answer was right. Checking *which tools fired in what order*
is a separate dimension. The cheap version is returning the tool-call log from `Ask`
alongside the answer.

**Substring matching only works because every answer is a number or a name.** Ask
*"summarise the payment terms"* and there is nothing to assert on. That is where LLM-as-judge
comes in — a second model call grading against a rubric. Worth knowing the name; it is
non-deterministic and can be wrong in both directions, so it is a later tool, not a better one.

**Seven cases over one document is not a test suite.** It is enough to catch a regression in
mechanics already understood. It would not detect a relevance degradation across a real corpus.

**The console preview truncates at 100 characters.** `SearchDocuments` returns three chunks,
so roughly a third of what the model actually saw is visible. Fine while green; actively
misleading when diagnosing a retrieval failure.

---

## 9. Spike status

Learning list complete. Evals were the last item; Semantic Kernel is deliberately skipped —
a keyword rather than a necessity, and layering an abstraction over mechanics just learned
by hand is a net loss.

**Three confirmed failure findings now:**

1. Line-split chunking severed a paragraph; the model produced a confidently wrong VAT total
   of 17,598.05 against a correct 14,913.60. Reverting fixed it.
2. Model arithmetic is unreliable. The fix is a function, not a better prompt.
3. A function fixes calculation but not extraction — broken chunking fed `14,793.60` into a
   correct penalty calculation and produced a perfectly computed 443.81. Caught by an eval
   asserting the right answer, not by predicting the wrong one.

Plus one data bug: position-keyed documents orphan rows when the chunk count shrinks, and a
green run concealed it.

**Next:** leave the spike and start the customs document project — ASP.NET Core API, .NET 10.
Upload → Document Intelligence → chunk and embed → Azure AI Search → Q&A with citations →
review queue for low-confidence extractions. This spike is the reference, not the starting point.

---

## 10. Interview material from this session

- Evals are a test suite over a non-deterministic system; the assertion problem is that
  output is free text, so you assert on the critical fact after normalising formatting
- Assert the right answer, not the absence of a known wrong one — wrong answers are unbounded,
  and the same root cause produced a different number on its second appearance
- Tools fix arithmetic but not argument extraction; a correct function on a corrupted input
  yields a confident, well-formatted, internally consistent wrong answer
- Output eval vs trajectory eval, and what each misses
- LLM-as-judge as the escape hatch for open-ended answers, and why it is a later tool
- Position-keyed search documents orphan rows when re-indexing a smaller set; real systems
  key by content hash or swap an index alias
- A green suite hid a data bug — reading the retrieval output caught it, not the assertions
