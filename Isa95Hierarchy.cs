using System;
using System.Collections.Generic;
using System.Text;

namespace I3X4Kusto
{
    /// <summary>
    /// Builds an in-memory ISA-95 containment tree from the flat variable-level metadata rows returned by
    /// <see cref="ADXDataService.GetIsa95LeafAssets"/>.
    ///
    /// The OPC UA metadata only contains variable rows (one per Subject + Name) whose owning asset's ISA-95
    /// path lives in the Enterprise/Site/Area/Line/Workcell columns; the intermediate container levels are not
    /// materialized as their own rows. This class synthesizes:
    ///   - the container nodes (one per distinct ISA-95 path prefix); the deepest container represents the
    ///     asset itself and carries its Subject / DataSetWriterId so asset-level values can be answered,
    ///   - the variable leaf nodes (one per Name) directly under their deepest container,
    /// so the I3X hierarchy can be walked from the top-most container all the way down to individual
    /// value-bearing variables.
    ///
    /// It is generic across producers:
    ///   - UA Cloud Publisher populates all five levels.
    ///   - Azure IoT Operations populates only a subset (station as Workcell, location as Site).
    /// Empty levels are skipped, so each asset's path is the ordered sequence of its populated levels and the
    /// tree adapts to whatever levels a given OPC UA server provides.
    /// </summary>
    public sealed class Isa95Hierarchy
    {
        /// <summary>Prefix that distinguishes synthetic container ElementIds from asset/variable ids.</summary>
        public const string ContainerIdPrefix = "isa95:";

        /// <summary>
        /// Namespace URI for the synthetic ISA-95 structural container levels (Enterprise/Site/Area/Line/
        /// Workcell). The I3X specification requires every level to carry a namespaceUri, so intermediate
        /// container nodes (which are not backed by an OPC UA node of their own) are qualified with this URI.
        /// </summary>
        public const string Isa95NamespaceUri = "urn:opcfoundation:ua:isa95";

        /// <summary>Separator between an asset Subject and a variable Name in a variable leaf ElementId.</summary>
        public const string VariableIdSeparator = "::";

        // ISA-95 levels from the top of the containment hierarchy to the bottom.
        private static readonly string[] LevelNames = { "Enterprise", "Site", "Area", "Line", "Workcell" };

        private readonly Dictionary<string, Node> _byId = new(StringComparer.Ordinal);
        private readonly List<Node> _roots = new();

        public Isa95Hierarchy(IEnumerable<Dictionary<string, object>> variableRows)
        {
            foreach (var row in variableRows)
            {
                // Ordered, non-empty ISA-95 path segments for this asset (level name + value).
                var path = new List<(string Level, string Value)>();
                foreach (var level in LevelNames)
                {
                    string value = Str(row, level);
                    if (!string.IsNullOrEmpty(value))
                    {
                        path.Add((level, value));
                    }
                }

                // Ensure a container node exists for every prefix of the path, linking parent -> child.
                Node parent = null;
                for (int depth = 1; depth <= path.Count; depth++)
                {
                    string containerId = BuildContainerId(path, depth);
                    if (!_byId.TryGetValue(containerId, out var node))
                    {
                        var (level, value) = path[depth - 1];
                        node = new Node
                        {
                            ElementId = containerId,
                            DisplayName = value,
                            Level = level,
                            Kind = NodeKind.Container,
                            NamespaceUri = Isa95NamespaceUri,
                            ParentId = parent?.ElementId
                        };
                        _byId[containerId] = node;
                        AttachChild(parent, node);
                    }
                    parent = node;
                }

                // The deepest container represents the asset; record its Subject so asset-level values work.
                // Variables hang directly under it. When there is no ISA-95 context, the asset itself becomes
                // a root node so its variables remain discoverable.
                Node asset = parent != null ? AttachAssetToContainer(parent, row) : AddRootAsset(row);

                // Variable leaf node (one per Name), attached under its asset/container.
                AddVariable(row, asset);
            }
        }

        /// <summary>Top-most container nodes (or root asset nodes when no ISA-95 context exists).</summary>
        public IReadOnlyList<Node> Roots => _roots;

        /// <summary>All nodes (containers, assets and variables) keyed by ElementId.</summary>
        public IReadOnlyDictionary<string, Node> ById => _byId;

        public bool TryGet(string elementId, out Node node) => _byId.TryGetValue(elementId, out node);

        /// <summary>Direct children of the given node.</summary>
        public IReadOnlyList<Node> ChildrenOf(string elementId) =>
            _byId.TryGetValue(elementId, out var node) ? node.Children : Array.Empty<Node>();

        /// <summary>Direct parent of the given node, or null for a root.</summary>
        public Node ParentOf(string elementId) =>
            _byId.TryGetValue(elementId, out var node) && node.ParentId != null && _byId.TryGetValue(node.ParentId, out var parent)
                ? parent
                : null;

        /// <summary>Builds the variable leaf ElementId for a Subject + Name pair.</summary>
        public static string VariableId(string subject, string name) =>
            subject + VariableIdSeparator + name;

        private Node AttachAssetToContainer(Node container, Dictionary<string, object> row)
        {
            // The deepest ISA-95 container is the asset. Record its Subject (first writer wins if several
            // assets somehow share the same Workcell path) so asset-level value queries can resolve telemetry.
            if (container.Subject == null)
            {
                container.Subject = Str(row, "Subject");
                string assetNamespace = Str(row, "NamespaceUri");
                if (!string.IsNullOrEmpty(assetNamespace))
                {
                    container.NamespaceUri = assetNamespace;
                }
            }
            return container;
        }

        private Node AddRootAsset(Dictionary<string, object> row)
        {
            // No ISA-95 context: expose the asset itself as a root node keyed by its Subject.
            string subject = Str(row, "Subject");
            if (string.IsNullOrEmpty(subject))
            {
                return null;
            }

            if (!_byId.TryGetValue(subject, out var asset))
            {
                asset = new Node
                {
                    ElementId = subject,
                    DisplayName = subject,
                    Kind = NodeKind.Container,
                    Level = "Asset",
                    Subject = subject,
                    NamespaceUri = string.IsNullOrEmpty(Str(row, "NamespaceUri"))
                        ? Isa95NamespaceUri
                        : Str(row, "NamespaceUri")
                };

                _byId[subject] = asset;
                AttachChild(null, asset);
            }
            return asset;
        }

        private void AddVariable(Dictionary<string, object> row, Node asset)
        {
            string subject = Str(row, "Subject");
            string name = Str(row, "Name");

            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(name))
            {
                return;
            }

            string variableId = VariableId(subject, name);

            if (_byId.ContainsKey(variableId))
            {
                return;
            }

            var variable = new Node
            {
                ElementId = variableId,
                DisplayName = name,
                Type = Str(row, "Type"),
                DataType = Str(row, "DataType"),
                BuiltInType = Str(row, "BuiltInType"),
                NodeId = Str(row, "NodeId"),
                NamespaceUri = Str(row, "NamespaceUri"),
                Subject = subject,
                VariableName = name,
                Level = "Variable",
                Kind = NodeKind.Variable,
                ParentId = asset?.ElementId
            };

            _byId[variableId] = variable;
            AttachChild(asset, variable);
        }

        private void AttachChild(Node parent, Node child)
        {
            if (parent == null)
            {
                _roots.Add(child);
            }
            else
            {
                parent.Children.Add(child);
            }
        }

        // Deterministic, collision-safe container id built from the path prefix up to the given depth,
        // e.g. "isa95:/Contoso/Munich/Assembly". Segment separators in values are escaped.
        private static string BuildContainerId(IReadOnlyList<(string Level, string Value)> path, int depth)
        {
            var sb = new StringBuilder(ContainerIdPrefix);

            for (int i = 0; i < depth; i++)
            {
                sb.Append('/').Append(Escape(path[i].Value));
            }

            return sb.ToString();
        }

        private static string Escape(string value) =>
            value.Replace("%", "%25").Replace("/", "%2F");

        private static string Str(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        /// <summary>Kind of node in the synthesized ISA-95 tree.</summary>
        public enum NodeKind
        {
            Container, // an ISA-95 level (Enterprise/Site/Area/Line/Workcell); the deepest is the asset
            Variable   // an individual OPC UA variable (telemetry field) of an asset
        }

        /// <summary>A node in the synthesized ISA-95 tree: a container level (asset) or a variable.</summary>
        public sealed class Node
        {
            public string ElementId { get; init; }

            public string DisplayName { get; init; }

            public string Type { get; set; }

            public string NamespaceUri { get; set; }

            public string Level { get; init; }

            public NodeKind Kind { get; init; }

            public string ParentId { get; init; }

            /// <summary>OPC UA DataType NodeId of a variable (from the DataSetMetaData field).</summary>
            public string DataType { get; init; }

            /// <summary>OPC UA BuiltInType id of a variable (from the DataSetMetaData field).</summary>
            public string BuiltInType { get; init; }

            /// <summary>OPC UA NodeId of a variable's node (from the DataSetMetaData field).</summary>
            public string NodeId { get; init; }

            /// <summary>
            /// Owning asset's Subject. Set on Variable nodes and on the deepest ("asset") container so its
            /// asset-level telemetry can be resolved.
            /// </summary>
            public string Subject { get; set; }

            /// <summary>OPC UA field name (set for Variable nodes only).</summary>
            public string VariableName { get; init; }

            /// <summary>
            /// The OPC UA type token for a variable, used as the (namespace-qualified) type id and display
            /// name. Prefers the human-readable OPC UA built-in type name (e.g. "String", "Double"), then the
            /// standard DataType name resolved from its NodeId (e.g. "i=27" => "Integer"), then the raw
            /// DataType NodeId, then the field's Type/Description, then its Name. The same token is used for
            /// objects and /objecttypes so their ids always agree.
            /// </summary>
            public string TypeToken =>
                OpcUaBuiltInType.GetDisplayName(BuiltInType)
                ?? OpcUaBuiltInType.GetDataTypeName(DataType)
                ?? (!string.IsNullOrEmpty(DataType) ? DataType
                : !string.IsNullOrEmpty(Type) ? Type
                : VariableName);

            /// <summary>True when this container node represents an asset (carries a Subject).</summary>
            public bool IsAsset => Kind == NodeKind.Container && !string.IsNullOrEmpty(Subject);

            public bool IsContainer => Kind == NodeKind.Container;

            public List<Node> Children { get; } = new();
        }
    }
}
