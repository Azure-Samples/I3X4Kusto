using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace I3X4Kusto
{
    public class ADXDataService
    {
        private ICslQueryProvider _queryProvider = null;

        // Cache for the ISA-95 leaf-asset metadata. This query (opcua_metadata_lkv) is executed by nearly
        // every /objects, /objecttypes, /namespaces and /subscriptions request; OPC UA metadata changes
        // rarely, so caching it for a short TTL drastically cuts the query volume against ADX / the Fabric
        // Eventhouse and avoids 429 throttling on small (e.g. F2) capacities. TTL is configurable via
        // I3X_METADATA_CACHE_SECONDS (default 60, minimum 0 = disabled).
        private readonly object _metadataCacheLock = new();
        private List<Dictionary<string, object>> _metadataCache;
        private DateTime _metadataCacheUtc = DateTime.MinValue;

        public void Connect()
        {
            // connect to ADX cluster
            string adxClusterName = Environment.GetEnvironmentVariable("ADX_HOST");
            string adxDBName = Environment.GetEnvironmentVariable("ADX_DB");
            string aadAppID = Environment.GetEnvironmentVariable("ADX_APPLICATION_ID");

            if (!string.IsNullOrEmpty(adxClusterName) && !string.IsNullOrEmpty(adxDBName))
            {
                KustoConnectionStringBuilder connectionString;
                if (string.IsNullOrEmpty(aadAppID))
                {
                    connectionString = new KustoConnectionStringBuilder(adxClusterName, adxDBName)
                        .WithAadAzureTokenCredentialsAuthentication(new DefaultAzureCredential(new DefaultAzureCredentialOptions() { TenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") }));
                }
                else
                {
                    connectionString = new KustoConnectionStringBuilder(adxClusterName, adxDBName)
                        .WithAadUserManagedIdentity(aadAppID);
                }

                _queryProvider = KustoClientFactory.CreateCslQueryProvider(connectionString);
            }
        }

        public void Dispose()
        {
            if (_queryProvider != null)
            {
                _queryProvider.Dispose();
                _queryProvider = null;
            }
        }

        /// <summary>
        /// KQL prelude that defines <c>NamespaceBySubject</c>: for every metadata Subject, the OPC UA
        /// namespace URI to use. It is generic across OPC UA servers and producers:
        ///   - UA Cloud Publisher embeds the URI in DataSetName (';nsu=<uri>;'), surfaced as NamespaceUri.
        ///   - Producers that omit it (e.g. Azure IoT Operations) still carry the server namespace table in
        ///     the DataSetMetaData "Namespaces" array (OPC UA PubSub spec, Part 14); the first non-base entry
        ///     (index &gt; 0) is the application's own namespace.
        /// </summary>
        public const string NamespaceBySubjectPrelude =
              "let NamespaceBySubject = union\r\n"
            + "    (opcua_metadata_lkv\r\n"
            + "     | where isnotempty(NamespaceUri)\r\n"
            + "     | distinct Subject, NamespaceUri),\r\n"
            + "    (opcua_metadata_raw\r\n"
            + "     | extend Subject = tostring(split(tostring(payload[\"DataSetWriterId\"]), \"/\")[0])\r\n"
            + "     | extend nss = todynamic(payload[\"MetaData\"][\"Namespaces\"])\r\n"
            + "     | mv-expand with_itemindex=idx NamespaceUri = nss to typeof(string)\r\n"
            + "     | where idx > 0 and isnotempty(NamespaceUri)\r\n"
            + "     | summarize NamespaceUri = take_any(NamespaceUri) by Subject)\r\n"
            + "    | summarize NamespaceUri = take_any(NamespaceUri) by Subject;\r\n";

        /// <summary>
        /// Returns a KQL fragment that resolves <c>NamespaceUri</c> for every row of the current tabular
        /// result. It <c>lookup</c>s the per-Subject namespace from <see cref="NamespaceBySubjectPrelude"/>
        /// and coalesces it with any URI already present on the row, so both producers yield a real value.
        /// The prelude must be prepended to the query, and the piped-in rows must expose <c>NamespaceUri</c>
        /// and the given subject column.
        /// </summary>
        public static string ResolveNamespaceUri(string subjectColumn = "Subject") =>
              "| extend __rowNs = NamespaceUri\r\n"
            + "| project-away NamespaceUri\r\n"
            + "| lookup kind=leftouter (NamespaceBySubject | project Subject, __lkpNs = NamespaceUri) "
            + "on $left." + subjectColumn + " == $right.Subject\r\n"
            + "| extend NamespaceUri = coalesce(iif(isnotempty(__rowNs), __rowNs, ''), __lkpNs, '')\r\n"
            + "| project-away __rowNs, __lkpNs";

        /// <summary>
        /// Escapes and quotes an array of values for use in a KQL <c>in()</c> operator.
        /// </summary>
        public static string ToKqlStringList(string[] values)
        {
            return string.Join(", ", values.Select(v =>
                "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
        }

        /// <summary>
        /// Executes a KQL query and returns every result row as a column-name → value dictionary.
        /// </summary>
        public List<Dictionary<string, object>> RunQueryRows(string query)
        {
            var rows = new List<Dictionary<string, object>>();

            if (_queryProvider == null)
            {
                return rows;
            }

            // Retry on throttling (HTTP 429 / TooManyRequests). Small analytics capacities (e.g. a Fabric F2
            // Eventhouse) throttle bursts of queries; a short bounded exponential backoff lets transient
            // throttles recover instead of surfacing as errors. Combined with the ISA-95 metadata cache this
            // keeps the query volume within capacity limits.
            const int maxAttempts = 4;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ClientRequestProperties clientRequestProperties = new ClientRequestProperties()
                {
                    ClientRequestId = Guid.NewGuid().ToString()
                };

                try
                {
                    using (IDataReader reader = _queryProvider.ExecuteQuery(query, clientRequestProperties))
                    {
                        while (reader.Read())
                        {
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                try
                                {
                                    if (reader.GetValue(i) != null)
                                    {
                                        row[reader.GetName(i)] = reader.GetValue(i);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine(ex.Message);
                                }
                            }
                            rows.Add(row);
                        }
                    }

                    return rows;
                }
                catch (Exception ex) when (IsThrottling(ex) && attempt < maxAttempts)
                {
                    // Exponential backoff: 0.5s, 1s, 2s (with a little jitter).
                    int delayMs = (int)(500 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 250);
                    Console.WriteLine($"RunADXQuery: throttled (429), retry {attempt}/{maxAttempts - 1} after {delayMs}ms");
                    rows.Clear();
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("RunADXQuery: " + ex.Message);
                    return rows;
                }
            }

            return rows;
        }

        // True when the exception represents a Kusto throttling / TooManyRequests (429) condition.
        private static bool IsThrottling(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                string m = e.Message;
                if (!string.IsNullOrEmpty(m) &&
                    (m.IndexOf("429", StringComparison.Ordinal) >= 0 ||
                     m.IndexOf("TooManyRequests", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.IndexOf("Throttl", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns one row per OPC UA variable (Subject + Name) with its owning asset's ISA-95 path
        /// (Enterprise/Site/Area/Line/Workcell), identity (Subject/DisplayName/Name), the generically resolved
        /// NamespaceUri, and the OPC UA type information (DataType NodeId, BuiltInType and the field NodeId).
        /// This is the raw input the ISA-95 hierarchy builder uses to synthesize the container tree, the
        /// per-Subject asset object, and the per-variable leaf nodes with their proper OPC UA type ids.
        /// </summary>
        public List<Dictionary<string, object>> GetIsa95LeafAssets()
        {
            int ttlSeconds = GetMetadataCacheSeconds();
            if (ttlSeconds > 0)
            {
                lock (_metadataCacheLock)
                {
                    if (_metadataCache != null &&
                        (DateTime.UtcNow - _metadataCacheUtc).TotalSeconds < ttlSeconds)
                    {
                        return _metadataCache;
                    }
                }
            }

            var rows = QueryIsa95LeafAssets();

            if (ttlSeconds > 0)
            {
                lock (_metadataCacheLock)
                {
                    _metadataCache = rows;
                    _metadataCacheUtc = DateTime.UtcNow;
                }
            }

            return rows;
        }

        private List<Dictionary<string, object>> QueryIsa95LeafAssets()
        {
            string query = NamespaceBySubjectPrelude
                         + "opcua_metadata_lkv\r\n"
                         + ResolveNamespaceUri() + "\r\n"
                         + "| distinct Subject, DataSetName, Name, DisplayName, Type, DataType, BuiltInType, NodeId, "
                         + "NamespaceUri, Enterprise, Site, Area, Line, Workcell\r\n"
                         + "| project Subject, DataSetName, Name, DisplayName, Type, DataType, BuiltInType, NodeId, "
                         + "NamespaceUri, Enterprise, Site, Area, Line, Workcell";

            return RunQueryRows(query);
        }

        // TTL for the ISA-95 metadata cache, in seconds. Default 60, minimum 0 (disables the cache).
        private static int GetMetadataCacheSeconds()
        {
            string raw = Environment.GetEnvironmentVariable("I3X_METADATA_CACHE_SECONDS");
            if (int.TryParse(raw, out int seconds) && seconds >= 0)
            {
                return seconds;
            }

            return 60;
        }
    }
}
