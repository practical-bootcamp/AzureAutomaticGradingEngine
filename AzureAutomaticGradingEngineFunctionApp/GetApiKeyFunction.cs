using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using AzureAutomaticGradingEngineFunctionApp.Dao;
using AzureAutomaticGradingEngineFunctionApp.Helper;

namespace AzureAutomaticGradingEngineFunctionApp
{
    public static class GetApiKeyFunction
    {
        [Function(nameof(GetApiKeyFunction))]
        public static IActionResult Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req, FunctionContext context,
            ILogger log)
        {
            log.LogInformation("GetApiKeyFunction HTTP trigger function processed a request.");

            var config = new Config(context);
            var dao = new LabCredentialDao(config, log);
            string course = req.Query["course"];
            string email = req.Query["email"];
            var credential = dao.Get(course, email);
            return new JsonResult(credential);
        }
    }
}
