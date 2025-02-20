﻿using Hangfire.Common;
using Hangfire.RecurringJobAdmin.Models;
using Hangfire.Storage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Hangfire.RecurringJobAdmin.Core
{
    public static class JobAgent
    {
        public const string tagRecurringJob = "recurring-job";
        public const string tagStopJob = "recurring-jobs-stop";
        public static void StartBackgroundJob(string JobId)
        {
            using (var connection = JobStorage.Current.GetConnection())
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveFromSet(tagStopJob, JobId);
                transaction.AddToSet($"{tagRecurringJob}s", JobId);
                transaction.Commit();
            }
        }
        public static void StopBackgroundJob(string JobId)
        {
            using (var connection = JobStorage.Current.GetConnection())
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveFromSet($"{tagRecurringJob}s", JobId);
                transaction.AddToSet($"{tagStopJob}", JobId);
                transaction.Commit();
            }
        }
        public static void RemoveBackgroundJob(string JobId)
        {
            using (var connection = JobStorage.Current.GetConnection())
            using (connection.AcquireDistributedLock($"lock:recurring-job:{JobId}", TimeSpan.FromSeconds(15)))
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveHash($"recurring-job:{JobId}");
                transaction.RemoveFromSet("recurring-jobs", JobId);
                transaction.RemoveFromSet(tagStopJob, JobId);

                transaction.Commit();
            }
        }

        public static List<PeriodicJob> GetAllJobStopped()
        {
            var outPut = new List<PeriodicJob>();
            using (var connection = JobStorage.Current.GetConnection())
            {
                var allJobStopped = connection.GetAllItemsFromSet(tagStopJob);

                allJobStopped.ToList().ForEach(jobId =>
                {
                    var dto = new PeriodicJob();

                    var dataJob = connection.GetAllEntriesFromHash($"{tagRecurringJob}:{jobId}");
                    dto.Id = jobId;
                    dto.TimeZoneId = "UTC"; // Default

                    try
                    {
                        if (dataJob.TryGetValue("Job", out var payload) && !String.IsNullOrWhiteSpace(payload))
                        {
                            var invocationData = InvocationData.DeserializePayload(payload);
                            var job = invocationData.DeserializeJob();
                            dto.Method = job.Method.Name;
                            dto.Class = job.Type.FullName;
                            dto.Arguments = job.Args;
                            dto.ArgumentsTypes = job.Method.GetParameters()?.Select(p => p.ParameterType.FullName);
                        }
                    }
                    catch (JobLoadException ex)
                    {
                        dto.Error = ex.Message;
                    }

                    if (dataJob.ContainsKey("TimeZoneId"))
                    {
                        dto.TimeZoneId = dataJob["TimeZoneId"];
                    }

                    if (dataJob.ContainsKey("NextExecution"))
                    {
                        var tempNextExecution = JobHelper.DeserializeNullableDateTime(dataJob["NextExecution"]);

                        dto.NextExecution = tempNextExecution;
                    }

                    if (dataJob.ContainsKey("LastJobId") && !string.IsNullOrWhiteSpace(dataJob["LastJobId"]))
                    {
                        dto.LastJobId = dataJob["LastJobId"];

                        var stateData = connection.GetStateData(dto.LastJobId);
                        if (stateData != null)
                        {
                            dto.LastJobState = stateData.Name;
                        }
                    }

                    if (dataJob.ContainsKey("Queue"))
                    {
                        dto.Queue = dataJob["Queue"];
                    }

                    if (dataJob.ContainsKey("Cron"))
                    {
                        dto.Cron = dataJob["Cron"];
                    }

                    if (dataJob.ContainsKey("LastExecution"))
                    {

                        var tempLastExecution = JobHelper.DeserializeNullableDateTime(dataJob["LastExecution"]);

                        dto.LastExecution = tempLastExecution;
                    }

                    if (dataJob.ContainsKey("CreatedAt"))
                    {
                        dto.CreatedAt = JobHelper.DeserializeNullableDateTime(dataJob["CreatedAt"]);
                        dto.CreatedAt = dto.CreatedAt.HasValue ? dto.CreatedAt.Value.ChangeTimeZone(dto.TimeZoneId) : new DateTime();
                    }

                    if (dataJob.TryGetValue("Error", out var error) && !String.IsNullOrEmpty(error))
                    {
                        dto.Error = error;
                    }

                    dto.Removed = false;
                    dto.JobState = "Stopped";

                    outPut.Add(dto);

                });
            }
            return outPut;
        }

        public static List<RecurringJobDto> GetStoppedRecurringJobs(IStorageConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException("connection");
            }

            HashSet<string> allItemsFromSet = connection.GetAllItemsFromSet(tagStopJob);
            var getRecurringJobDtos = typeof(Hangfire.Storage.StorageConnectionExtensions).GetMethod("GetRecurringJobDtos", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return (List<RecurringJobDto>)getRecurringJobDtos?.Invoke(null, new object[] { connection, allItemsFromSet });
        }

        public static bool IsValidJobId(string jobId, string tag = tagRecurringJob)
        {
            var result = false;
            using (var connection = JobStorage.Current.GetConnection())
            {
                var job = connection.GetAllEntriesFromHash($"{tag}:{jobId}");

                result = job != null;
            }
            return result;
        }
    }
}
