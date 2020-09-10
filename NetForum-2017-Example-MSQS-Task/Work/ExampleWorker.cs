using Avectra.netForum.Common;
using Avectra.netForum.Data;
using log4net;
using NetForumMsqsExample.Utility;
using Newtonsoft.Json.Linq;
using System;

namespace NetForumMsqsExample.Work
{
    class ExampleWorker : IDisposable
    {
        public void Dispose()
        {
            // Nothing to do, but if our process was more involved we might need to tear down some instanciated objects
        }

        /// <summary>
        /// Creates a Individual record from a JObject
        /// </summary>
        /// <param name="messageId">ID of the message, for logging</param>
        /// <param name="message">The fields used to create our individual</param>
        /// <param name="log">Logger</param>
        public void Execute(string messageId, JObject message, ILog log)
        {
            using (var connection = DataUtils.GetConnection())
            {
                using (var transaction = connection.BeginTransaction())
                {
                    FacadeClass facade;

                    // Check if they already exist (using email as unique property)
                    facade = Existing(message.Value<string>("eml_address"), connection, transaction);

                    // If they don't exist, create them
                    if (facade == null)
                    {
                        facade = Create(message, connection, transaction);
                        
                        log.Info($"{messageId} Create individual: ({facade.CurrentKey}) {facade.GetValue("cst_recno")}");
                    }
                    else
                    {
                        // Otherwise just note they already exist
                        log.Info($"{messageId} Existing individual: ({facade.CurrentKey}) {facade.GetValue("cst_recno")}");
                    }

                    // For this process, we are done with the facade object
                    facade.Dispose();

                    // Make sure to commit since Create() writes to the database
                    transaction.Commit();
                }
            }
        }

        private FacadeClass Existing(string eml_address, NfDbConnection connection, NfDbTransaction transaction)
        {
            // Quick and dirty duplicate check by email address
            using (var cmd = new NfDbCommand("SELECT cst_key FROM co_customer WHERE cst_delete_flag = 0 AND cst_type = 'Individual' AND cst_eml_address_dn = @eml_address", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@eml_address", eml_address);

                var result = cmd.ExecuteScalar();

                // If a match was found
                if (result is Guid cst_key)
                {
                    // Instanciate a facade and pull down their record
                    var facade = FacadeObjectFactory.CreateIndividual();
                    facade.CurrentKey = cst_key.ToString();
                    facade.SelectByKey(connection, transaction);
                    return facade;
                }
            }

            return null;
        }

        private FacadeClass Create(JObject fields, NfDbConnection connection, NfDbTransaction transaction)
        {
            var facade = FacadeObjectFactory.CreateIndividual();

            // Copy the JObject fields into the FacadeClass
            facade.Merge(fields);

            // Try to add them to the database
            var error = facade.Insert(connection, transaction);

            // Throw on error
            if (error != null && error.HasError)
            {
                throw new ApplicationException(error.Message);
            }

            // Tell NetForum we are done with this operation so it can do any background tasks
            // Neccessity of this varies by Facade Object
            facade.ProcessRoundTripEvents(connection, transaction);

            // Reload it so all the computed/denormalized fields are present
            facade.CurrentKey = facade.GetValue("cst_key");
            facade.SelectByKey(connection, transaction);

            return facade;
        }
    }
}
