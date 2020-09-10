using Avectra.netForum.Common;
using log4net;
using NetForumMsqsExample.Work;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Messaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

[assembly: log4net.Config.Repository("NetForumMsqsExample")]
[assembly: log4net.Config.XmlConfigurator(Watch = true)]


namespace NetForumMsqsExample
{
    class Program
    {
        public static readonly ILog Log;
        public static readonly CancellationTokenSource cancellationTokenSource;
        public static CancellationToken cancellationToken => cancellationTokenSource.Token;

        static Program()
        {
            // Used to catch CTRL+C at the command line
            cancellationTokenSource = new CancellationTokenSource();

            // Setup logger
            log4net.Util.LogLog.InternalDebugging = true;
            log4net.Util.LogLog.QuietMode = false;
            Log = LogManager.GetLogger(typeof(Program));

            // Tell NetForum who we are
            Config.SuperUser = true;
            Config.CurrentUserName = "NetForumMsqsExample";
        }

        static void Main(string[] args)
        {
            try
            {
                // Get app settings. See App.config for descriptions
                var appSettings = ConfigurationManager.AppSettings;
                var limitHours = appSettings.Get("LimitHours") == "true";
                var queueName = appSettings.Get("QueueName");
                Tuple<int, int> hours = null;

                if (limitHours)
                {
                    try
                    {
                        var parts = appSettings.Get("RunHours").Split(',').Select(h => int.Parse(h)).ToArray();
                        hours = new Tuple<int, int>(parts[0], parts[1]);
                    } catch (Exception e)
                    {
                        Log.Error(e);
                        limitHours = false;
                    }
                }
                
                // Catch CTRL+C
                Console.CancelKeyPress += Console_CancelKeyPress;
                Log.Info($"Found {Config.SystemOptions.Count} system options.");

                // Verify the queue exists
                if (MessageQueue.Exists(queueName) == false)
                {
                    throw new ApplicationException($"Queue {queueName} does not exist.");
                }

                // Instanciate the queue
                using (var queue = new MessageQueue(queueName, QueueAccessMode.Receive))
                {
                    // Tell the queue we want the contents as a raw string since we are using JSON instead of XML
                    queue.Formatter = new BinaryMessageFormatter();

                    // Start Listening thread
                    Receive(queue, limitHours, hours, cancellationToken).Wait(cancellationToken);
                }

            }
            catch (Exception e)
            {
                Log.Error(e.Message, e);
            }
        }

        private static Task Receive(MessageQueue queue, CancellationToken cancellationToken)
        {
            return Receive(queue, false, null, cancellationToken);
        }

        private static async Task Receive(MessageQueue queue, bool limitHours, Tuple<int, int> hours, CancellationToken cancellationToken)
        {
            Log.Info("Starting Receive");

            // Create our worker
            using (var worker = new ExampleWorker())
            {
                // Loop until cancelled
                while (cancellationToken.IsCancellationRequested == false)
                {
                    // Check if we are limiting run hours
                    if (limitHours && hours != null)
                    {
                        var hour = DateTime.Now.Hour;
                        if (hour >= hours.Item2 && hour < hours.Item1)
                        {
                            Log.Info("Hour limit reached");
                            break;
                        }
                    }

                    Console.WriteLine();
                    
                    // Read each queue message in a transaction so failures don't destory the message
                    using (var transaction = new MessageQueueTransaction())
                    {
                        Message message = null;

                        try
                        {
                            transaction.Begin();

                            // Wait update to 1 minute for a message to appear in the queue
                            message = queue.Receive(TimeSpan.FromMinutes(1), transaction);

                            Log.Info($"{message.Id} {message.Label}: Received");

                            // Get the JSON body
                            var body = await Read(message);

                            // Some error handling
                            if (body == null)
                            {
                                Log.Warn($"{message.Id} {message.Label}: Body could not be parsed");
                                transaction.Commit();
                                continue;
                            }

                            // Execute the message
                            worker.Execute(message.Id, body, Log);

                            Log.Info($"{message.Id} {message.Label}: Complete");

                            // Tell the queue we are done with the message so it is removed
                            transaction.Commit();
                        }
                        catch (TaskCanceledException)
                        {
                            Log.Warn($"{message?.Id} {message.Label}: TaskCanceledException");

                            // Tell the queue the message was not processed so it is returned
                            transaction.Abort();
                        }
                        catch (MessageQueueException e)
                        {
                            Log.Warn(e.Message);
                            break;
                        }
                        catch (Exception e)
                        {
                            Log.Error($"{message?.Id} {message.Label}: {e.Message}", e);

                            // Abort instead of commit if you want to leave it in the queue
                            transaction.Commit();
                        }
                    }
                }
            }
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            if (cancellationTokenSource.IsCancellationRequested == false)
            {
                // Trigger a cancel so the current message can finish processing gracefully
                cancellationTokenSource.Cancel();

                // Interupt the cancel request so our code can finish
                e.Cancel = true;

                Log.Warn("Console Interrupt requested");
            }
        }

        private static async Task<JObject> Read(Message message)
        {
            // Basic XML reader to get the JSON body
            // The message is in the format <string>{ raw json }</string>
            try
            {
                using (var reader = XmlReader.Create(message.BodyStream, new XmlReaderSettings() { Async = true }))
                {
                    while (await reader.ReadAsync())
                    {
                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (reader.Name != "string")
                        {
                            continue;
                        }

                        var content = await reader.ReadElementContentAsStringAsync();

                        return JObject.Parse(content);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e.Message, e);
            }

            return null;
        }
    }
}
