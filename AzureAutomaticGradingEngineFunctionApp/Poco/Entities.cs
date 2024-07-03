using AzureAutomaticGradingEngineFunctionApp.Model;
using System;
using System.Collections.Generic;

namespace AzureAutomaticGradingEngineFunctionApp.Poco
{
    public class AssignmentPoco
    {
        public required string Name { get; set; }
        public required string TeacherEmail { get; set; }
        public bool SendMarkEmailToStudents { get; set; }
        public DateTime GradeTime { get; set; }
        public required ClassContext Context { get; set; }

    }

    public class ClassContext
    {
        public required string GraderUrl { get; set; }
        public required List<Student> Students { get; set; }
    }

    public class EmailMessage
    {
        public required string To { get; set; }
        public required string Subject { get; set; }
        public required string Body { get; set; }
    }

    public class ClassGradingJob
    {
        public required AssignmentPoco assignment { get; set; }
        public required string graderUrl { get; set; }
        public required List<Student> students { get; set; }
    }

    public class SingleGradingJob
    {
        public required AssignmentPoco assignment { get; set; }
        public required string graderUrl { get; set; }
        public required Student student { get; set; }
    }


    public class MarkDetails
    {
        public required Dictionary<string, int> Mark { get; set; }
        public required Dictionary<string, DateTime> CompleteTime { get; set; }
    }

}
