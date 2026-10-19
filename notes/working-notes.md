# Working notes

How to pick this up again, and how to work on it. Addressed to me, not to a visitor —
the README is the front door; this is the bit that keeps sessions from starting cold.

---

## Start here each session

1. Read the README (2 minutes).
2. `cd LlmSpike && dotnet run` to confirm it still works.
3. Pick up at **What's next** in the README.

The session write-ups in this folder hold the reasoning behind specific decisions. Read them
when you need the *why* behind one thing, not to re-orient — that's the README's job.

---

## How to work on it

- **Time budget is 1–2 hours a week**, so context loss between sessions is the dominant
  cost. That's what the README is for. Keep it current; it's cheaper than re-reading these
  notes.

- **Ask for the shape, not the finished code.** The spike went well partly because the code
  was typed and read before being run. Getting the mechanism and the API names, then writing
  it yourself, produces compiler errors — and fixing those is what sticks.

- **The working test for whether a session landed:** can you walk someone through the file
  and justify the structure without reading the comments?

- **Green is not proof.** Session 4's eval suite passed 7/7 against an index containing nine
  orphaned chunks. Reading the retrieval output caught it; the assertions did not.
