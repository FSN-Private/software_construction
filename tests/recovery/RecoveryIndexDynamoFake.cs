using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

internal class RecoveryIndexDynamoFake : DispatchProxy
{
    private readonly Dictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> _items = [];
    private int _pageSize = 1;

    public int ScanCalls { get; private set; }
    public int QueryCalls { get; private set; }
    public Action<GetItemRequest>? BeforeGet { get; set; }
    public Dictionary<string, AttributeValue>? Read(string pk, string sk) =>
        _items.TryGetValue((pk, sk), out var item) ? Clone(item) : null;

    public static IAmazonDynamoDB Create(IEnumerable<Dictionary<string, AttributeValue>> seed,
        int pageSize, out RecoveryIndexDynamoFake fake)
    {
        var client = DispatchProxy.Create<IAmazonDynamoDB, RecoveryIndexDynamoFake>();
        fake = (RecoveryIndexDynamoFake)(object)client;
        fake._pageSize = pageSize;
        foreach (var item in seed) fake._items[fake.ReadKey(item)] = Clone(item);
        return client;
    }

    public void Remove(string pk, string sk) => _items.Remove((pk, sk));
    public void Replace(Dictionary<string, AttributeValue> item) => _items[ReadKey(item)] = Clone(item);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null || args is null) throw new InvalidOperationException("method_missing");
        return targetMethod.Name switch
        {
            nameof(IAmazonDynamoDB.ScanAsync) => Task.FromResult(Scan((ScanRequest)args[0]!)),
            nameof(IAmazonDynamoDB.QueryAsync) => Task.FromResult(Query((QueryRequest)args[0]!)),
            nameof(IAmazonDynamoDB.GetItemAsync) => Task.FromResult(Get((GetItemRequest)args[0]!)),
            nameof(IAmazonDynamoDB.PutItemAsync) => Task.FromResult(Put((PutItemRequest)args[0]!)),
            nameof(IAmazonDynamoDB.UpdateItemAsync) => Task.FromResult(Update((UpdateItemRequest)args[0]!)),
            _ => throw new NotSupportedException($"RecoveryIndexDynamoFake.{targetMethod.Name}")
        };
    }

    private ScanResponse Scan(ScanRequest request)
    {
        ScanCalls++;
        var all = _items.Values.OrderBy(item => ReadKey(item).Pk, StringComparer.Ordinal)
            .ThenBy(item => ReadKey(item).Sk, StringComparer.Ordinal).ToArray();
        var (items, lastKey) = Page(all, request.ExclusiveStartKey, Math.Min(_pageSize, request.Limit ?? _pageSize));
        return new ScanResponse
        {
            Items = items.Select(Clone).ToList(),
            ScannedCount = items.Count,
            LastEvaluatedKey = lastKey
        };
    }

    private QueryResponse Query(QueryRequest request)
    {
        if (request.ConsistentRead != true) throw new InvalidOperationException("query_not_strongly_consistent");
        QueryCalls++;
        var partition = request.ExpressionAttributeValues[":pk"].S;
        var all = _items.Values.Where(item => ReadKey(item).Pk == partition)
            .Where(item => !request.ExpressionAttributeValues.TryGetValue(":point", out var prefix) ||
                           ReadKey(item).Sk.StartsWith(prefix.S, StringComparison.Ordinal))
            .OrderBy(item => ReadKey(item).Sk, StringComparer.Ordinal).ToArray();
        var (items, lastKey) = Page(all, request.ExclusiveStartKey, Math.Min(_pageSize, request.Limit ?? _pageSize));
        return new QueryResponse
        {
            Items = items.Select(Clone).ToList(),
            Count = items.Count,
            ScannedCount = items.Count,
            LastEvaluatedKey = lastKey
        };
    }

    private GetItemResponse Get(GetItemRequest request)
    {
        if (request.ConsistentRead != true) throw new InvalidOperationException("get_not_strongly_consistent");
        BeforeGet?.Invoke(request);
        var key = ReadKey(request.Key);
        return new GetItemResponse
        {
            Item = _items.TryGetValue(key, out var item) ? Clone(item) : new Dictionary<string, AttributeValue>()
        };
    }

    private PutItemResponse Put(PutItemRequest request)
    {
        var key = ReadKey(request.Item);
        if (_items.ContainsKey(key)) throw new ConditionalCheckFailedException("conditional_failed");
        _items.Add(key, Clone(request.Item));
        return new PutItemResponse();
    }

    private UpdateItemResponse Update(UpdateItemRequest request)
    {
        var key = ReadKey(request.Key);
        if (!_items.TryGetValue(key, out var item) || ReadString(item, "status") != "building" ||
            ReadNumber(item, "indexVersion") != 1)
            throw new ConditionalCheckFailedException("conditional_failed");

        item["status"] = Clone(request.ExpressionAttributeValues[":complete"]);
        item["bootstrapPointSetSha256"] = Clone(request.ExpressionAttributeValues[":digest"]);
        item["bootstrapPointCount"] = Clone(request.ExpressionAttributeValues[":count"]);
        item["indexedPointCount"] = Clone(request.ExpressionAttributeValues[":count"]);
        item.Remove("buildingAtUtc");
        return new UpdateItemResponse { Attributes = Clone(item) };
    }

    private (List<Dictionary<string, AttributeValue>> Items, Dictionary<string, AttributeValue> LastKey) Page(
        IReadOnlyList<Dictionary<string, AttributeValue>> all,
        Dictionary<string, AttributeValue>? startKey, int size)
    {
        var start = 0;
        if (startKey is { Count: > 0 })
        {
            var after = ReadKey(startKey);
            var cursor = all.ToList().FindIndex(item => ReadKey(item) == after);
            if (cursor < 0) throw new InvalidOperationException("pagination_cursor_missing");
            start = cursor + 1;
        }
        var selected = all.Skip(start).Take(Math.Max(1, size)).ToList();
        var next = start + selected.Count < all.Count && selected.Count > 0
            ? Key(ReadKey(selected[^1]).Pk, ReadKey(selected[^1]).Sk)
            : new Dictionary<string, AttributeValue>();
        return (selected, next);
    }

    private (string Pk, string Sk) ReadKey(Dictionary<string, AttributeValue> item) =>
        (ReadString(item, "pk"), ReadString(item, "sk"));

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new()
    {
        ["pk"] = String(pk),
        ["sk"] = String(sk)
    };

    private static string ReadString(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S ?? "" : "";

    private static long ReadNumber(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && long.TryParse(value.N, out var number) ? number : -1;

    private static AttributeValue String(string value) => new() { S = value };

    private static Dictionary<string, AttributeValue> Clone(Dictionary<string, AttributeValue> item) =>
        item.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);

    private static AttributeValue Clone(AttributeValue value) => new()
    {
        S = value.S,
        N = value.N,
        BOOL = value.BOOL
    };
}
