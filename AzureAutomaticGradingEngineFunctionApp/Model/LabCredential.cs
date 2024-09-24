using System.Runtime.Serialization;
using Azure;
using ITableEntity = Azure.Data.Tables.ITableEntity;

namespace AzureAutomaticGradingEngineFunctionApp.Model;

internal class LabCredential : ITableEntity
{
 public string AppId { get; set; }
    public string DisplayName { get; set; }
    public string Password { get; set; }
    public string Tenant { get; set; }

    public string Email { get; set; }
    public string SubscriptionId { get; set; }
    public string PartitionKey { get; set; }
    public string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    [IgnoreDataMember] public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();

 }