namespace I3X4Kusto
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    // ---------------------------------------------------------------------
    // Response envelopes (match the i3X OpenAPI spec)
    // ---------------------------------------------------------------------

    /// <summary>Envelope for single-result endpoints: { success, result }.</summary>
    public sealed record SuccessResponse<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("result")] T Result);

    /// <summary>Envelope for bulk endpoints: { success, results: [...] }.</summary>
    public sealed record BulkResponse<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("results")] IReadOnlyList<BulkResultItem<T>> Results);

    /// <summary>A single item within a <see cref="BulkResponse{T}"/>.</summary>
    public sealed record BulkResultItem<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("elementId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ElementId { get; init; }

        [JsonPropertyName("subscriptionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SubscriptionId { get; init; }

        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public T Result { get; init; }

        [JsonPropertyName("responseDetail")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ErrorDetail ResponseDetail { get; init; }

        public static BulkResultItem<T> Ok(string elementId, T result) =>
            new() { Success = true, ElementId = elementId, Result = result };

        public static BulkResultItem<T> NotFound(string elementId, string detail) =>
            new()
            {
                Success = false,
                ElementId = elementId,
                ResponseDetail = new ErrorDetail("Not Found", 404, detail)
            };
    }

    /// <summary>Problem-details style error payload.</summary>
    public sealed record ErrorDetail(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("detail")] string Detail);

    /// <summary>Top-level error envelope: { success:false, responseDetail }.</summary>
    public sealed record ErrorResponse(
        [property: JsonPropertyName("responseDetail")] ErrorDetail ResponseDetail,
        [property: JsonPropertyName("success")] bool Success = false);

    // ---------------------------------------------------------------------
    // Server info
    // ---------------------------------------------------------------------

    public sealed record ServerInfo(
        [property: JsonPropertyName("specVersion")] string SpecVersion,
        [property: JsonPropertyName("capabilities")] ServerCapabilities Capabilities,
        [property: JsonPropertyName("serverVersion")] string ServerVersion = null,
        [property: JsonPropertyName("serverName")] string ServerName = null);

    public sealed record ServerCapabilities(
        [property: JsonPropertyName("query")] QueryCapabilities Query,
        [property: JsonPropertyName("update")] UpdateCapabilities Update,
        [property: JsonPropertyName("subscribe")] SubscribeCapabilities Subscribe);

    public sealed record QueryCapabilities(
        [property: JsonPropertyName("history")] bool History);

    public sealed record UpdateCapabilities(
        [property: JsonPropertyName("current")] bool Current,
        [property: JsonPropertyName("history")] bool History);

    public sealed record SubscribeCapabilities(
        [property: JsonPropertyName("stream")] bool Stream);

    // ---------------------------------------------------------------------
    // Explore domain models
    // ---------------------------------------------------------------------

    public sealed record Namespace(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("displayName")] string DisplayName);

    public sealed record ObjectTypeResponse(
        [property: JsonPropertyName("elementId")] string ElementId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("namespaceUri")] string NamespaceUri,
        [property: JsonPropertyName("sourceTypeId")] string SourceTypeId,
        [property: JsonPropertyName("schema")] Dictionary<string, object> Schema,
        [property: JsonPropertyName("version")] string Version = null,
        [property: JsonPropertyName("related")] Dictionary<string, object> Related = null);

    public sealed record RelationshipType(
        [property: JsonPropertyName("elementId")] string ElementId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("namespaceUri")] string NamespaceUri,
        [property: JsonPropertyName("relationshipId")] string RelationshipId,
        [property: JsonPropertyName("reverseOf")] string ReverseOf);

    public sealed record ObjectInstanceResponse(
        [property: JsonPropertyName("elementId")] string ElementId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("typeElementId")] string TypeElementId,
        [property: JsonPropertyName("isComposition")] bool IsComposition,
        [property: JsonPropertyName("parentId")] string ParentId = null,
        [property: JsonPropertyName("isExtended")] bool IsExtended = false,
        [property: JsonPropertyName("metadata")] ObjectInstanceMetadata Metadata = null);

    public sealed record ObjectInstanceMetadata(
        [property: JsonPropertyName("typeNamespaceUri")] string TypeNamespaceUri = null,
        [property: JsonPropertyName("sourceTypeId")] string SourceTypeId = null,
        [property: JsonPropertyName("description")] string Description = null,
        [property: JsonPropertyName("relationships")] Dictionary<string, object> Relationships = null,
        [property: JsonPropertyName("schemaExtensions")] Dictionary<string, object> SchemaExtensions = null,
        [property: JsonPropertyName("system")] Dictionary<string, object> System = null);

    public sealed record RelatedObjectResult(
        [property: JsonPropertyName("sourceRelationship")] string SourceRelationship,
        [property: JsonPropertyName("object")] ObjectInstanceResponse Object);

    // ---------------------------------------------------------------------
    // Value / history models
    // ---------------------------------------------------------------------

    /// <summary>Value-Quality-Timestamp tuple.</summary>
    public sealed record VQT(
        [property: JsonPropertyName("value")] object Value,
        [property: JsonPropertyName("quality")] string Quality,
        [property: JsonPropertyName("timestamp")] string Timestamp);

    public sealed record CurrentValueResult(
        [property: JsonPropertyName("isComposition")] bool IsComposition,
        [property: JsonPropertyName("value")] object Value,
        [property: JsonPropertyName("quality")] string Quality,
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("components")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Dictionary<string, VQT> Components = null);

    public sealed record HistoricalValueResult(
        [property: JsonPropertyName("isComposition")] bool IsComposition,
        [property: JsonPropertyName("values")] IReadOnlyList<VQT> Values,
        [property: JsonPropertyName("components")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Dictionary<string, object> Components = null);

    // ---------------------------------------------------------------------
    // Request models (match spec schema names)
    // ---------------------------------------------------------------------

    public sealed record GetObjectTypesRequest(
        [property: JsonPropertyName("elementIds")] string[] ElementIds);

    public sealed record GetRelationshipTypesRequest(
        [property: JsonPropertyName("elementIds")] string[] ElementIds);

    public sealed record GetObjectsRequest(
        [property: JsonPropertyName("elementIds")] string[] ElementIds,
        [property: JsonPropertyName("includeMetadata")] bool IncludeMetadata = false);

    public sealed record GetRelatedObjectsRequest(
        [property: JsonPropertyName("elementIds")] string[] ElementIds,
        [property: JsonPropertyName("relationshipType")] string RelationshipType = null,
        [property: JsonPropertyName("includeMetadata")] bool IncludeMetadata = false);

    public sealed record GetObjectValueRequest(
        [property: JsonPropertyName("elementIds")] string[] ElementIds,
        [property: JsonPropertyName("maxDepth")] int MaxDepth = 1);

    public sealed record GetObjectHistoryRequest(
        [property: JsonPropertyName("elementIds")] string[] ElementIds,
        [property: JsonPropertyName("startTime")] string StartTime = null,
        [property: JsonPropertyName("endTime")] string EndTime = null,
        [property: JsonPropertyName("maxDepth")] int MaxDepth = 1);
}
