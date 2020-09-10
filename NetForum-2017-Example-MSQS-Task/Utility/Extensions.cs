using Avectra.netForum.Data;
using Newtonsoft.Json.Linq;

namespace NetForumMsqsExample.Utility
{
    static class Extensions
    {
        /// <summary>
        /// Copy the values from <em>update</em> to the named fields on the <em>facade</em>,
        /// skipping hidden, readyonly, and otherwise noneditable fields
        /// </summary>
        /// <param name="facade">The FacadeClass to copy the values onto.</param>
        /// <param name="update">The JSON object to read values from.</param>
        public static void Merge(this FacadeClass facade, JObject update)
        {
            foreach (var pair in update)
            {
                var token = pair.Value;
                var field = facade.GetField(pair.Key);

                // Make sure the field exists
                if (field == null)
                {
                    continue;
                }

                // Skip read-only fields
                if (!field.CanUpdate || field.Properties.ReadOnly || field.Properties.ReadOnlyEdit || field.Properties.NotEditable)
                {
                    continue;
                }

                // Make sure its a scalar type
                if (token is JValue jvalue)
                {
                    field.ValueNative = jvalue.Value;
                }
            }
        }
    }
}
