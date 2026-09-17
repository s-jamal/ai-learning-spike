# LLM Spike — Session 3: Function Calling

Giving the model the ability to invoke C# methods. Continues from `session2-azure-ai-search.md`.

---

## 1. The mechanic

The model cannot execute anything. That is the single most important thing to understand here.

What actually happens:

1. You describe your C# methods to the model as "tools" — name, description, JSON Schema for the arguments
2. The model replies with `FinishReason == ChatFinishReason.ToolCalls` and a structured request: *call `calculate_late_penalty` with `{"amount": 14913.6, "daysLate": 30}`*
3. **Your code** decides whether to honour that and runs the method
4. The result goes back into the message list as a `ToolChatMessage`
5. You call the model again; it now sees the result and writes the final answer

Step 3 is the security boundary. A model requesting `DeleteAllRecords` is just JSON until your dispatch code chooses to act on it.

Everything downstream of this — tool use, agents, orchestration frameworks — is elaboration on this one loop.

---

## 2. Code structure

Three parts.

### Ordinary C# methods

```csharp
static decimal CalculateLatePenalty(decimal amount, int daysLate)
{
    decimal dailyRate = 0.001m;   // 0.1 percent per day
    return amount * dailyRate * daysLate;
}
```

Nothing AI about these. Plain arithmetic.

### Tool descriptions

```csharp
var penaltyTool = ChatTool.CreateFunctionTool(
    functionName: "calculate_late_penalty",
    functionDescription: "Calculates the late payment penalty on a customs amount. "
                       + "Use this whenever the user asks about penalties for late payment.",
    functionParameters: BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "amount":   { "type": "number",  "description": "The outstanding amount in USD" },
            "daysLate": { "type": "integer", "description": "How many days the payment is overdue" }
        },
        "required": ["amount", "daysLate"]
    }
    """));
```

**The description text is load-bearing.** It is the only thing the model has to decide whether a tool applies. Vague descriptions produce tools that never fire, or fire on the wrong questions. Treat it as part of the interface, not a comment.

### The dispatch loop

```csharp
while (true)
{
    var completion = await chat.CompleteChatAsync(messages, options);
    var result = completion.Value;

    if (result.FinishReason != ChatFinishReason.ToolCalls)
    {
        Console.WriteLine("Answer: " + result.Content[0].Text);
        return;
    }

    // The model's own request must go into the history first,
    // or the tool results have nothing to attach to.
    messages.Add(new AssistantChatMessage(result));

    foreach (var toolCall in result.ToolCalls)
    {
        string output = RunTool(toolCall.FunctionName, toolCall.FunctionArguments);
        messages.Add(new ToolChatMessage(toolCall.Id, output));
    }
}
```

The loop exists because one answer may need several rounds of tool calls. It exits when the model returns a normal completion.

`messages` grows as this runs: system → user → assistant (tool request) → tool (result) → assistant (answer). That growing list **is** the conversation state. Watching it in the debugger makes statelessness concrete.

---

## 3. What was observed

### Natural language to typed arguments

Input: *"The total payable is 14,913.60 USD. What is the penalty if I pay 30 days late?"*

Model produced: `{"amount": 14913.6, "daysLate": 30}`

Comma stripped, types correct, ready for `GetDecimal()` and `GetInt32()`. This extraction is the genuinely useful capability — turning prose into structured parameters.

### Arithmetic is now correct — because C# does it

14,913.60 × 0.001 × 30 = 447.41. Correct.

**Direct contrast with session 1**, where the model tried to compute a VAT total in its head and produced 17,598.05 instead of 14,913.60. Same class of problem. The fix is not a better prompt — it is not asking a language model to calculate. Give it a function.

That pairs with the chunking finding as the spike's second confirmed result.

### Parallel tool calls waste money

Question: *"What's the penalty for 30 days late, in euros?"*

Actual sequence:

1. `calculate_late_penalty(14913.6, 30)` → 447.41 USD ✓
2. `convert_currency(14913.6, EUR)` → 13,720.51 EUR ← **never asked for**
3. `convert_currency(447.41, EUR)` → 411.62 EUR ✓

Calls 1 and 2 fired in the same round, in parallel. The model speculatively converted the total before it knew the penalty, then looped and made the call it actually needed.

Final answer was correct, but that is a wasted API call and extra latency on every such query. Mitigations: prompt guidance ("call one tool at a time, wait for results"), or disabling parallel tool calls in `ChatCompletionOptions`.

Worth having as an answer to "what's the downside of tool use."

### Tool errors are conversation, not exceptions

`ConvertCurrency` throws `ArgumentException` on unknown currencies. Initially there was no try/catch — the exception would have killed the process.

It never fired, because the model read *"Supported: EUR, AZN, GBP"* in the tool description and declined without calling. **That is not error handling.** Model output is non-deterministic; a rephrased question, a longer conversation, or a redeployed model version could all produce the call.

Fix:

```csharp
static string RunTool(string name, BinaryData arguments)
{
    try
    {
        // dispatch...
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}
```

Confirmed by temporarily removing the supported-currency list from the description. The model then attempted `JPY`, the method threw, and `Error: Unknown currency: JPY` came back as a tool result. The model read it and replied sensibly — no crash, no hallucinated rate, no retry loop.

**Principle: never let the model's good behaviour be your error handling.** It is not deterministic and it is not a contract.

**Caveat for real code:** `ex.Message` here is harmless because the exceptions are ours. A database exception could leak a connection string or schema detail straight into the model's context and out to the user. Production catches specific types and returns sanitised text, logging full detail separately.

Both layers are worth keeping: the description prevents the common case, the catch handles the rest.

### Scope control lives in the system prompt

Same question — *"What is the capital of France?"* — three different behaviours across the spike:

| System prompt | Result |
|---|---|
| `"Answer using only the context provided..."` (RAG, session 2) | Declined |
| `"You are a customs assistant. Use the provided tools when relevant."` | **Answered "Paris"** |
| `"...Only answer questions about customs declarations and payments. Decline anything else."` | Declined |

Tools do not restrict what the model knows. They extend what it can do. Scope restriction is a separate concern and it lives in the prompt.

This is the cheapest guardrail available and also the weakest — it is an instruction, not enforcement, and it can be talked around with rephrasing or role-play framings. Real systems layer: scoped system prompt → content filters (Azure attaches DefaultV2 to deployments) → input/output validation in code → logging.

That layering is the guardrails topic, still ahead.

---

## 4. Debugging in VS Code

Requires the C# Dev Kit extension. F5 generates `.vscode/launch.json` on first run.

F9 breakpoint, F5 continue, F10 step over, F11 step into, Shift+F11 step out.

Most useful breakpoints in this code:

- The `FinishReason` check — the fork between "wants a tool" and "has an answer"
- Inside the `foreach` over `ToolCalls` — inspect `FunctionName` and `FunctionArguments` as raw JSON
- First line of `RunTool` — now in ordinary C# that the model requested but did not run
- The second loop iteration's `CompleteChatAsync` — expand `messages` and watch the conversation state grow

Async code makes stepping jump around. Prefer breakpoints plus F5 over stepping through awaits line by line.

---

## 5. Spike status

Mechanics covered end to end:

- Stateless chat calls, message arrays, system prompts
- Embeddings, dimensions, cosine similarity written by hand
- Chunking — with a confirmed failure caused by bad boundaries
- Fixed-k vs threshold retrieval, and why thresholds do not transfer between implementations
- Azure AI Search, HNSW, vector vs keyword vs hybrid
- Function calling, the dispatch loop, tool error handling
- Prompt-level scope control

**Two confirmed failure findings** (both good interview material):

1. Line-split chunking severed a paragraph and the model produced a confidently wrong VAT total of 17,598.05 against a correct 14,913.60. Reverting the split fixed it — single variable, opposite outcome.
2. Model arithmetic is unreliable; giving it a function is the fix, not a better prompt.

**Next:** merge RAG and tools into one program. That is the actual architecture of the customs project — a question comes in, the model either answers from retrieved chunks, calls a function, or both. *"What's the penalty on this declaration if I pay 30 days late?"* needs retrieval to find the amount and a function to compute the penalty.

After that: leave the spike behind and start the real project. Semantic Kernel and evals/guardrails remain on the learning list.

---

## 6. Working notes

Switching to Claude Code (VS Code extension or the desktop app's Code tab) makes sense once the real project starts — it reads the solution, runs builds, and edits files directly rather than passing code blocks back and forth. Hand it these notes files as the opening context.

Caveat worth respecting: the spike went well partly because the code was typed and read before running. Claude Code hands over working diffs, which is faster and less educational. Read what it changes.
