using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AzureAutomaticGradingEngineFunctionApp.Helper;
using AzureAutomaticGradingEngineFunctionApp.Poco;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace AzureAutomaticGradingEngineFunctionApp
{
    public static partial class ScheduleGraderFunction
    {
        [Function(nameof(DailyGrader))]
        public static async Task DailyGrader(
            [TimerTrigger("0 0 0 * * *")] TimerInfo myTimer,
            [DurableClient] DurableTaskClient client,        
            ILogger log)
        {
            if (myTimer.IsPastDue)
            {
                log.LogInformation("Timer is running late!");
            }
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(DailyGraderOrchestrationFunction));
            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }

        [Function(nameof(DailyGraderOrchestrationFunction))]
        public static async Task DailyGraderOrchestrationFunction(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var assignments = await context.CallActivityAsync<List<AssignmentPoco>>(nameof(GetAssignmentList), true);
            await AssignmentTasks(context, nameof(SaveTodayMarkJson), assignments);
            Console.WriteLine("Completed!");
        }

        [Function(nameof(SaveTodayMarkJson))]
        public static async Task SaveTodayMarkJson([ActivityTrigger] AssignmentPoco assignment,
            FunctionContext executionContext,
            ILogger log)
        {
            var todayMarks = await GradeReportFunction.CalculateMarks(log, executionContext, assignment.Name, true);
            var blobName = string.Format(CultureInfo.InvariantCulture, assignment.Name + "/{0:yyyy/MM/dd/HH/mm}/todayMarks.json", assignment.GradeTime);
            await CloudStorage.SaveJsonReport(executionContext, blobName, todayMarks);
            blobName = assignment.Name + "/todayMarks.json";
            await CloudStorage.SaveJsonReport(executionContext, blobName, todayMarks);
        }
    }
}