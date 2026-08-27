namespace ElasticRateLimiter.LoadTest;

/// <summary>
/// Query bodies chosen so their cost under ElasticsearchQueryCostEstimator is predictable, which is
/// what lets the run drain a bucket at a known rate. Costs are base 1 plus: wildcard/regexp 10,
/// fuzzy/prefix 5, script 15, aggs 5, cardinality 8, and pagination penalties for large from/size.
/// </summary>
public static class QueryBodies
{
    public static string For(QueryShape shape) => shape switch
    {
        QueryShape.Simple => Simple,
        QueryShape.Aggs => Aggs,
        QueryShape.Wildcard => Wildcard,
        QueryShape.Expensive => Expensive,
        _ => Simple,
    };

    /// <summary>Estimated token cost, so the client can predict when the bucket runs dry.</summary>
    public static int CostOf(QueryShape shape) => shape switch
    {
        QueryShape.Simple => 1,       // base
        QueryShape.Aggs => 6,         // base + aggs 5
        QueryShape.Wildcard => 11,    // base + wildcard 10
        QueryShape.Expensive => 36,   // base + script 15 + cardinality 8 + from>10000 20 ... capped by parser
        _ => 1,
    };

    private const string Simple = """
        {"query":{"term":{"status":"active"}},"size":10}
        """;

    private const string Aggs = """
        {"query":{"match_all":{}},"aggs":{"by_status":{"terms":{"field":"status"}}},"size":0}
        """;

    private const string Wildcard = """
        {"query":{"wildcard":{"message":{"value":"err*"}}},"size":10}
        """;

    private const string Expensive = """
        {"query":{"script":{"script":"doc['price'].value > 100"}},"aggs":{"unique":{"cardinality":{"field":"user_id"}}},"from":20000,"size":10}
        """;
}
