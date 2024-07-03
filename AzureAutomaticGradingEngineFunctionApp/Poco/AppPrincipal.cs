using System.Runtime.Serialization;

namespace AzureAutomaticGradingEngineFunctionApp.Poco;

[DataContract]
public class AppPrincipal : JsonBase<AppPrincipal>
{
    [DataMember] public required string appId;
    [DataMember] public required string displayName;
    [DataMember] public required string password;
    [DataMember] public required string tenant;
}