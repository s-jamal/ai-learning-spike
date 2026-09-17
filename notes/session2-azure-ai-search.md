# LLM Spike — Session 2: Azure AI Search

Replacing the in-memory cosine scan with a real vector store. Continues from `session1-rag-fundamentals.md`.

---

## 1. What changed

The previous session held documents and vectors in a `List<(string, float[])>` and scored every one with a hand-written `Cosine` function. That works at 5 chunks and falls over at scale — every query is a full scan.

Azure AI Search replaces the storage and the scan. The `Cosine` function is deleted. Everything else — chunking, embedding, prompt construction, the chat call — is unchanged.

**Resource created:**

- Azure AI Search, Free tier, `ai-learning` resource group, East US
- Free tier is shared infrastructure, can't scale, one per subscription. Fine for tutorials and small projects. Supports vector fields, which is all that's needed here.
- Package: `Azure.Search.Documents`
- Endpoint from Overview, admin key from Keys (admin writes, query key only reads)

---

## 2. Terminology, in plain terms

Names for things already built by hand in session 1.

| Term | What it actually is |
|---|---|
| **Vector** | The array of floats from `GenerateEmbeddingAsync`. Just an ordered list of numbers. |
| **Dimensions** | How many numbers in that array. 1536 for `text-embedding-3-small`, 3072 for `-large`. Must match the index field exactly. |
| **Chunk** | One piece of a document after splitting. The unit of retrieval. |
| **kNN** | k-nearest neighbours — "find the k closest vectors." The session 1 scoring loop was exact kNN. |
| **Exact kNN** | Compare against every vector. Correct, linear cost. Fine at 5, unusable at 500,000. |
| **HNSW** | Hierarchical Navigable Small World. Graph-based *approximate* nearest neighbour index. Layers of links let a query hop toward the target, touching hundreds of vectors instead of all of them. Trades a small amount of recall (~95%+ found) for a large speedup. |
| **BM25** | The classic keyword ranking algorithm. Scores on term frequency, inverse document frequency, length normalisation. Purely lexical — no shared words, no score. |
| **Hybrid search** | BM25 and vector search run together, rankings fused. |

**How much of this to know:** what it's for and when to pick it. Not the internals. "HNSW is a graph-based ANN index that trades a little recall for a lot of speed" is a complete interview answer. Same rule as a SQL B-tree index — know when to add one and what it costs on writes, not how pages split.

---

## 3. The code, explained

### Two clients

```csharp
var indexClient = new SearchIndexClient(new Uri(endpoint), credential);
var searchClient = new SearchClient(new Uri(endpoint), indexName, credential);
```

`SearchIndexClient` manages schemas — create, update, delete indexes. Operates on the service.
`SearchClient` works with documents inside one index — hence `indexName` in the constructor.

Roughly DDL vs DML.

### The index schema

```csharp
new SimpleField("id", SearchFieldDataType.String) { IsKey = true }
```
Exactly one key field per index, must be a string. `SimpleField` = stored but not full-text searchable.

```csharp
new SearchableField("content") { IsFilterable = false }
```
`SearchableField` = text gets analysed and indexed for keyword search. This is what enables BM25 later.

```csharp
new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
{
    IsSearchable = true,
    VectorSearchDimensions = 1536,
    VectorSearchProfileName = "vector-profile"
}
```
`Single` is .NET's name for `float`. `Collection(Single)` = array of floats. The dimension count must match the embedding model or upload rejects.

### The vector config indirection

```csharp
VectorSearch = new VectorSearch
{
    Algorithms = { new HnswAlgorithmConfiguration("hnsw-config") },
    Profiles = { new VectorSearchProfile("vector-profile", "hnsw-config") }
}
```

Field → profile → algorithm. Three levels for one vector field looks like ceremony, and at this scale it is. The reason it exists: a real index might have several vector fields (title, body, summary) sharing one algorithm config or using different ones.

```csharp
await indexClient.CreateOrUpdateIndexAsync(definition);
```
Idempotent. Safe to run every time the program starts.

### Uploading

```csharp
var searchDocument = new SearchDocument
{
    ["id"] = i.ToString(),
    ["content"] = chunks[i],
    ["contentVector"] = vector
};

await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(documents));
```

`SearchDocument` is a dictionary — string keys must match field names exactly. Used deliberately instead of a POCO: a class hits JSON casing mismatches (`ContentVector` vs `contentVector`) and fails confusingly. Real code would define a model and configure the serializer.

`Upload` = insert-or-replace. Also available: `Merge` (update existing fields only) and `Delete`.

```csharp
await Task.Delay(3000);
```
Indexing is asynchronous service-side — documents aren't queryable the instant upload returns. A sleep is a demo hack. Production polls or accepts eventual consistency.

### Querying

```csharp
var vectorQuery = new VectorizedQuery(queryVector) { KNearestNeighborsCount = 3 };
vectorQuery.Fields.Add("contentVector");

var options = new SearchOptions { VectorSearch = new VectorSearchOptions() };
options.VectorSearch.Queries.Add(vectorQuery);

var response = await searchClient.SearchAsync<SearchDocument>(null, options);
```

**"Vectorized"** means *you* supply the vector — the query was embedded in your own code. The alternative, `VectorizableTextQuery`, sends raw text and lets the service embed it, but requires wiring the OpenAI deployment into the search service. Doing it manually keeps the two services independent and the data flow visible.

`Queries` is a collection because multiple vector queries can run in one request against different fields, with results fused.

**The first argument is the keyword search text:**

- `null` → pure vector search, no keyword component
- `query` → hybrid: BM25 and vector both run, rankings fused

```csharp
await foreach (var result in response.Value.GetResultsAsync())
```
`await foreach` because results stream — the SDK pages behind the scenes. `result.Score` is nullable (some query types don't produce one).

---

## 4. Results and observations

Same four questions as session 1, now retrieving from the index. All answered correctly:

- Duty → 3,120 USD
- Late payment → 0.1 percent per day
- Total including VAT → 14,913.60 USD
- Capital of France → declined

### Scores are on a different scale

| Query | Session 1 (raw cosine) | Session 2 (Azure vector score) |
|---|---|---|
| Top relevant chunk | ~0.49 | ~0.72 |
| Off-topic (France) | ~0.04 | **~0.51** |

Azure normalises its scores. The 0.35 threshold from session 1 would pass *every* chunk here, including completely irrelevant ones. **Any threshold has to be recalibrated against observed values on the actual system** — it isn't portable between retrieval implementations.

The France query still declined correctly, but only because the grounding instruction held. Retrieval filtered nothing.

This code uses fixed k=3 rather than a threshold for that reason.

### Hybrid search: understood, not demonstrated

Tried querying the literal declaration number `CD-2024-08817`. Pure vector search found the right chunk.

That looks like a win but isn't evidence of anything, for two reasons:

1. **Only 5 chunks.** Vector search returns the top k regardless of match quality — there's no absolute bar, just ranking. With five candidates, three come back no matter what.
2. **The chunk is full of other signal.** It reads "Customs Declaration CD-2024-08817. Importer of record: Nordwind Logistics LLC, registered at 12 Neftchilar Avenue, Baku..." — "Customs Declaration" alone gives the embedding plenty to work with, even if the ID contributes nothing.

**The principle still holds:** embeddings are weak on exact identifiers — declaration numbers, SKUs, part numbers, error codes, postcodes — because an arbitrary token has no meaning to encode. BM25 matches the literal string and nails them. Production systems run both.

Seeing it fail needs a corpus of thousands of chunks where dozens are semantically similar and only the ID distinguishes them. Not reproducible at this scale.

**Note on switching to hybrid:** changing `null` to `query` drops scores to roughly 0.01–0.03. That's reciprocal rank fusion producing a different kind of number, not worse retrieval. Don't misread it.

---

## 5. Operational note

An admin key was exposed in a screenshot during this session and regenerated. Portal → search service → Keys → Regenerate primary, then update user secrets.

Worth the habit: admin keys write to the index. Query keys are read-only and are what a client application should use.

---

## 6. Where the spike stands

Complete for retrieval. Built and understood:

- Stateless chat calls, message arrays, system prompts
- Embeddings and vector dimensions
- Cosine similarity written by hand
- Chunking, and a confirmed failure caused by bad chunk boundaries
- Fixed-k vs threshold retrieval, and why thresholds don't transfer
- A real vector store with an HNSW index
- Vector vs keyword vs hybrid, and when each matters

**Next: function calling.** The model decides to invoke a C# method, the code runs it, the result goes back into the conversation. This is the step that changes what can be built — from answering questions about a document to actually doing something. Better interview material than the retrieval work, and closer to what agentic postings ask for.

Then: Semantic Kernel orchestration, evals and guardrails.

---

## 7. Interview material from this session

- Vector search misses exact identifiers because arbitrary tokens have no semantics to embed; hybrid covers the gap with BM25
- Retrieval score thresholds are implementation-specific and must be calibrated, not carried over
- HNSW trades recall for speed; exact kNN doesn't scale
- Indexing is eventually consistent — writes aren't immediately queryable
- Admin vs query keys as a least-privilege boundary
- `VectorizedQuery` vs `VectorizableTextQuery`: embedding in application code keeps services decoupled at the cost of an extra round trip
