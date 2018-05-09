﻿using NScrapy.Downloader;
using NScrapy.Infra;
using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
using System.Runtime.Serialization.Formatters.Binary;
using NScrapy.Scheduler.RedisExt;

namespace NScrapy.DownloaderShell
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var context = Downloader.DownloaderContext.Context;
            context.RunningMode = Downloader.DownloaderRunningMode.Distributed;
            context.Log.Info("Downloader Started");
            var receiveQueueName = DownloaderContext.Context.CurrentConfig["AppSettings:Scheduler.RedisExt:ReceiverQueue"];
            var responseTopicName = DownloaderContext.Context.CurrentConfig["AppSettings:Scheduler.RedisExt:ResponseTopic"];
            while (true)
            {
                var lockToken = new Guid().ToString();
                //Get Lock before we count the lengh of queue
                //In case multiple Downloader runs into this part and found there are 1 item in queue
                //Then one of the Downloader gets that item, results to other Downloader get nothing
                RedisManager.GetLock($"{receiveQueueName}.Lock", lockToken);
                if (RedisManager.Connection.GetDatabase().ListLength(receiveQueueName) > 0 &&
                   Downloader.Downloader.RunningDownloader < Downloader.Downloader.DownloaderPoolCapbility)
                {
                    
                    var requestMessage = string.Empty;
                    try
                    {                        
                        requestMessage = RedisManager.Connection.GetDatabase().ListRightPop(receiveQueueName);
                    }
                    catch (Exception ex)
                    {
                        DownloaderContext.Context.Log.Error($"Aquire lock failed!", ex);
                    }
                    finally
                    {
                        RedisManager.ReleaseLock($"{receiveQueueName}.Lock", lockToken);
                    }
                    ProcessRequestAndSendBack(responseTopicName, requestMessage);
                }
            }            
        }

        private static void ProcessRequestAndSendBack(string responseTopicName, string requestMessage)
        {
            var requestObj = JsonConvert.DeserializeObject<RedisRequestMessage>(requestMessage);
            var request = new HttpRequest()
            {
                URL = requestObj.URL
            };
            var result = Downloader.Downloader.SendRequestAsync(request);

            result.ContinueWith(async u =>
            {

                var resultPayload = string.Empty;
                try
                {
                    resultPayload = JsonConvert.SerializeObject(u.Result);
                }
                catch (Exception ex)
                {
                    DownloaderContext.Context.Log.Error($"Serialize response for {request.URL} failed!", ex);
                }

                resultPayload = await NScrapyHelper.CompressString(resultPayload);
                var responseMessage = new RedisResponseMessage()
                {
                    URL = requestObj.URL,
                    CallbacksFingerprint = requestObj.CallbacksFingerprint,
                    Payload = resultPayload
                };

                var publishResult = RedisManager.Connection.GetSubscriber().Publish(responseTopicName, $"{JsonConvert.SerializeObject(responseMessage)}");
                DownloaderContext.Context.Log.Info($"Sending request to {request.URL} success!");

            },
            TaskContinuationOptions.OnlyOnRanToCompletion);
            result.ContinueWith(u =>
            DownloaderContext.Context.Log.Info($"Sending request to {request.URL} failed", result.Exception.InnerException),
            TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
