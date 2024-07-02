using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Specialized;
using System.Globalization;

using System.Net.Mail;
using System.Web;
using AzureAutomaticGradingEngineFunctionApp.Helper;
using AzureAutomaticGradingEngineFunctionApp.Model;
using Cronos;

using Microsoft.Azure.Functions.Worker;
using AzureAutomaticGradingEngineFunctionApp.Dao;
using AzureAutomaticGradingEngineFunctionApp.Poco;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

using Microsoft.Azure.Functions.Worker.Http;

namespace AzureAutomaticGradingEngineFunctionApp
{
    public partial class ScheduleGraderFunction
    {
        private readonly ILogger _logger;
        public ScheduleGraderFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<ScheduleGraderFunction>();
        }

        [Function(nameof(ScheduleGrader))]
        public async Task ScheduleGrader(
            [TimerTrigger("0 */5 * * * *")] TimerInfo myTimer,
            [DurableClient] DurableTaskClient starter)
        {
            if (myTimer.IsPastDue)
            {
                _logger.LogInformation("Timer is running late!");
            }
            string instanceId = await starter.ScheduleNewOrchestrationInstanceAsync(nameof(GraderOrchestrationFunction));
            _logger.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }

        [Function(nameof(ManualRunGraderOrchestrationFunction))]
        public async Task<HttpResponseData> ManualRunGraderOrchestrationFunction(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequestData req, FunctionContext context,
            [DurableClient] DurableTaskClient starter
        )
        {
            var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync(nameof(GraderOrchestrationFunction));
            _logger.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [Function(nameof(GraderOrchestrationFunction))]
        public async Task GraderOrchestrationFunction(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            bool isManual = context.GetInput<bool>();
            var assignments = await context.CallActivityAsync<List<AssignmentPoco>>(nameof(GetAssignmentList), isManual);

            _logger.LogInformation($"context {context.InstanceId} {context.IsReplaying} Assignment Count = '{assignments.Count}' ignoreCronExpression:{isManual} ");
            var classJobs = new List<ClassGradingJob>();
            for (var i = 0; i < assignments.Count; i++)
            {
                classJobs.Add(ToClassGradingJob(assignments[i], _logger));
            }

            foreach (var classGradingJob in classJobs)
            {
                var gradingTasks = new Task[classGradingJob.students.Count];
                var i = 0;
                foreach (Student student in classGradingJob.students)
                {
                    gradingTasks[i] = context.CallActivityAsync<Task>(
                        nameof(RunAndSaveTestResult),
                        new SingleGradingJob
                        {
                            assignment = classGradingJob.assignment,
                            graderUrl = classGradingJob.graderUrl,
                            student = student
                        });
                    i++;
                }
                await Task.WhenAll(gradingTasks);
            }

            await AssignmentTasks(context, nameof(SaveAccumulatedMarkJson), assignments);

            _logger.LogInformation("Completed!");
        }



        public static async Task AssignmentTasks(TaskOrchestrationContext context, string activity, List<AssignmentPoco> assignments)
        {
            RetryPolicy retryPolicy = new RetryPolicy(
                firstRetryInterval: TimeSpan.FromSeconds(5),
                maxNumberOfAttempts: 1
                );
            var retryOptions = TaskOptions.FromRetryPolicy(retryPolicy);

            var task = new Task[assignments.Count];
            for (var i = 0; i < assignments.Count; i++)
            {
                task[i] = context.CallActivityAsync(activity, assignments[i], retryOptions);
            }
            await Task.WhenAll(task);
        }


        [Function(nameof(GetAssignmentList))]
        public List<AssignmentPoco> GetAssignmentList([ActivityTrigger] bool ignoreCronExpression, FunctionContext executionContext
    )
        {
            var storageAccount = CloudStorage.GetCloudStorageAccount(executionContext);

            var cloudTableClient = storageAccount.CreateCloudTableClient();
            var config = new Config(executionContext);

            var assignmentDao = new AssignmentDao(config, _logger);
            var labCredentialDao = new LabCredentialDao(config, _logger);
            var assignments = assignmentDao.GetAssignments();

            var now = DateTime.UtcNow;
            bool IsTriggered(Assignment assignment)
            {
                try
                {
                    var expression = CronExpression.Parse(assignment.CronExpression);
                    var nextOccurrence = expression.GetNextOccurrence(now.AddSeconds(-10));
                    var diff = nextOccurrence.HasValue ? Math.Abs(nextOccurrence.Value.Subtract(now).TotalSeconds) : -1;
                    var trigger = nextOccurrence.HasValue && diff < 10;
                    _logger.LogInformation($"{assignment.PartitionKey} {assignment.CronExpression} trigger: {trigger} , diff: {diff} seconds");
                    return trigger;
                }
                catch (Exception)
                {
                    _logger.LogInformation($"{assignment.PartitionKey} Invalid Cron Expression {assignment.CronExpression}!");
                    return false;
                }
            }

            if (!ignoreCronExpression)
                assignments = assignments.Where(IsTriggered).ToList();

            var results = new List<AssignmentPoco>();
            foreach (var assignment in assignments)
            {
                string graderUrl = assignment.GraderUrl;
                string project = assignment.PartitionKey;
                bool sendMarkEmailToStudents = assignment.SendMarkEmailToStudents.HasValue && assignment.SendMarkEmailToStudents.Value;
                var labCredentials = labCredentialDao.GetByProject(project);
                var students = labCredentials.Select(c => new Student
                {
                    email = c.RowKey,
                    credentials = new Confidential { appId = c.AppId, displayName = c.DisplayName, tenant = c.Tenant, password = c.Password }
                }).ToList();

                results.Add(new AssignmentPoco
                {
                    Name = project,
                    TeacherEmail = assignment.TeacherEmail,
                    SendMarkEmailToStudents = sendMarkEmailToStudents,
                    GradeTime = now,
                    Context = new ClassContext() { GraderUrl = graderUrl, Students = students }
                });

            }
            return results;
        }


        public static ClassGradingJob ToClassGradingJob(AssignmentPoco assignment, ILogger log)
        {
            var graderUrl = assignment.Context.GraderUrl;
            List<Student> students = assignment.Context.Students;
            log.LogInformation(assignment.Name + ":" + students.Count);
            return new ClassGradingJob() { assignment = assignment, graderUrl = graderUrl, students = students };
        }

        [Function(nameof(RunAndSaveTestResult))]
        public async Task RunAndSaveTestResult([ActivityTrigger] SingleGradingJob job, FunctionContext context)
        {
            var container = CloudStorage.GetCloudBlobContainer(context, "testresult");

#pragma warning disable IDE0017 // Simplify object initialization
            var client = new HttpClient();
#pragma warning restore IDE0017 // Simplify object initialization
            client.Timeout = TimeSpan.FromMinutes(8);
            var queryPair = new NameValueCollection();

            queryPair.Set("credentials", JsonConvert.SerializeObject(job.student.credentials));
            queryPair.Set("trace", job.student.email.ToString());

            var uri = new Uri(job.graderUrl + ToQueryString(queryPair));
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation("Calling grader URL for email -> " + job.student.email);
                var xml = await client.GetStringAsync(uri);

                await CloudStorage.SaveTestResult(container, job.assignment.Name, job.student.email.ToString(), xml, job.assignment.GradeTime);
                if (job.assignment.SendMarkEmailToStudents)
                    EmailTestResultToStudent(context, _logger, job.assignment.Name, job.student.email.ToString(), xml, job.assignment.GradeTime);
                watch.Stop();
                var elapsedMs = watch.ElapsedMilliseconds;
                _logger.LogInformation(job.student.email + " get test result in " + elapsedMs + "ms.");
            }
            catch (Exception ex)
            {
                _logger.LogInformation(job.student.email + " in error.");
                _logger.LogInformation(ex.ToString());
            }
        }

        private static string ToQueryString(NameValueCollection nvc)
        {
            var array = (
                from key in nvc.AllKeys
                from value in nvc.GetValues(key)
                select $"{HttpUtility.UrlEncode(key)}={HttpUtility.UrlEncode(value)}"
            ).ToArray();
            return "?" + string.Join("&", array);
        }

        private static void EmailTestResultToStudent(FunctionContext context, ILogger log, string assignment, string email, string xml, DateTime gradeTime)
        {
            var nUnitTestResult = GradeReportFunction.ParseNUnitTestResult(xml);
            var totalMark = nUnitTestResult.Sum(c => c.Value);

            var marks = string.Join("",
                nUnitTestResult.OrderBy(c => c.Key).Select(c => c.Key + ": " + c.Value + "\n").ToArray());

            var body = $@"
Dear Student,

You have just earned {totalMark} mark(s).

{marks}

Regards,
Azure Automatic Grading Engine
";
            var emailMessage = new EmailMessage
            {
                To = email,
                Subject = $"Your {assignment} Mark at {gradeTime}",
                Body = body
            };

            var config = new Config(context);
            var emailClient = new Email(config, log);
            emailClient.Send(emailMessage, new[] { Email.StringToAttachment(xml, "TestResult.txt", "text/plain") });
        }

        [Function(nameof(SaveAccumulatedMarkJson))]
        public async Task SaveAccumulatedMarkJson([ActivityTrigger] AssignmentPoco assignment,
            FunctionContext executionContext
            )
        {
            var accumulatedMarks = await GradeReportFunction.CalculateMarks(_logger, executionContext, assignment.Name, false);
            var blobName = string.Format(CultureInfo.InvariantCulture, assignment.Name + "/{0:yyyy/MM/dd/HH/mm}/accumulatedMarks.json", assignment.GradeTime);
            await CloudStorage.SaveJsonReport(executionContext, blobName, accumulatedMarks);
            blobName = assignment.Name + "/accumulatedMarks.json";
            await CloudStorage.SaveJsonReport(executionContext, blobName, accumulatedMarks);

            var workbookMemoryStream = new MemoryStream();
            GradeReportFunction.WriteWorkbookToMemoryStream(accumulatedMarks, workbookMemoryStream);

            blobName = string.Format(CultureInfo.InvariantCulture, assignment.Name + "/{0:yyyy/MM/dd/HH/mm}/marks.xlsx", assignment.GradeTime);
            await CloudStorage.SaveExcelReport(executionContext, blobName, workbookMemoryStream);
            blobName = assignment.Name + "/marks.xlsx";
            await CloudStorage.SaveExcelReport(executionContext, blobName, workbookMemoryStream);

            if (!string.IsNullOrEmpty(assignment.TeacherEmail))
            {
                var emailMessage = new EmailMessage
                {
                    To = assignment.TeacherEmail,
                    Subject = $"Accumulated Mark for {assignment.Name} on {assignment.GradeTime} (UTC)",
                    Body = @"Dear Teacher, 

Here are the accumulated mark report.

Regards,
Azure Automatic Grading Engine
"
                };

                var config = new Config(executionContext);
                var email = new Email(config, _logger);
                workbookMemoryStream = new MemoryStream(workbookMemoryStream.ToArray());
                var excelAttachment = new Attachment(workbookMemoryStream, "accumulatedMarks.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                var jsonAttachment = Email.StringToAttachment(JsonConvert.SerializeObject(accumulatedMarks),
                    "accumulatedMarks.json", "application/json");
                email.Send(emailMessage, new[] { excelAttachment, jsonAttachment });
            }
        }
    }
}