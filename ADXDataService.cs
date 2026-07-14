using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;

namespace I3X4Kusto
{
    public class ADXDataService
    {
        private ICslQueryProvider _queryProvider = null;

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

            ClientRequestProperties clientRequestProperties = new ClientRequestProperties()
            {
                ClientRequestId = Guid.NewGuid().ToString()
            };

            try
            {
                if (_queryProvider != null)
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("RunADXQuery: " + ex.Message);
            }

            return rows;
        }
    }
}
