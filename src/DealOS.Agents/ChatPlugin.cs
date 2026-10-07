using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Agents;
using DealOS.Agents.Infrastructure;
using DealOS.Agents.Runtime;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents
{
    /// <summary>
    /// Asynchronous step on gc_message Create: when a user (not the platform) writes in a conversation, the
    /// conversation's chat agent answers with a platform gc_message. The portal shows the reply by polling the
    /// conversation. Who may write in which conversation is enforced by Power Pages table permissions
    /// (conversation scoped to the user's account); here the conversation's account is the only identity used.
    /// </summary>
    public sealed class ChatPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            if (context.MessageName != "Create" || context.PrimaryEntityName != "gc_message") return;

            var target = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as Entity : null;
            if (target == null || (target.GetAttributeValue<bool?>("gc_isplatform") ?? false)) return; // our own replies
            var convRef = target.GetAttributeValue<EntityReference>("gc_conversation");
            if (convRef == null) return;

            var dv = new Dv(factory.CreateOrganizationService(context.UserId), factory.CreateOrganizationService(null));
            var conv = dv.System.RetrieveMultiple(new QueryExpression("gc_conversation")
            {
                ColumnSet = new ColumnSet("gc_assistant", "gc_counterparty", "gc_contact", "gc_channel"),
                Criteria = { Conditions = { new ConditionExpression("gc_conversationid", ConditionOperator.Equal, convRef.Id) } }
            }).Entities.FirstOrDefault();
            if (conv == null) return;
            var channel = conv.GetAttributeValue<OptionSetValue>("gc_channel");
            if (channel != null && channel.Value == Mail.MailChoice.ChannelEmail) return; // the Email Desk handles email threads

            var account = conv.GetAttributeValue<EntityReference>("gc_counterparty");
            if (account == null)
            {
                Reply(dv, conv.Id, "Your sign-in is not linked to a company yet. Finish your company profile on the onboarding page and I can help you then.");
                return;
            }
            var sender = target.GetAttributeValue<EntityReference>("gc_sendercontact");
            if (sender != null && !BelongsTo(dv, sender.Id, account.Id))
            {
                trace.Trace("Chat message {0}: sender contact {1} is not part of account {2}; not answered.", target.Id, sender.Id, account.Id);
                Reply(dv, conv.Id, "I can't answer this message because the sender isn't part of this company's account. Please sign in with your own account.");
                return;
            }

            var max = dv.SettingInt("chat.max_messages_per_hour", 30);
            var lastHour = dv.System.RetrieveMultiple(new QueryExpression("gc_message")
            {
                ColumnSet = new ColumnSet(false),
                TopCount = max + 1,
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("gc_conversation", ConditionOperator.Equal, conv.Id),
                        new ConditionExpression("gc_isplatform", ConditionOperator.NotEqual, true),
                        new ConditionExpression("createdon", ConditionOperator.GreaterThan, DateTime.UtcNow.AddHours(-1))
                    }
                }
            }).Entities.Count;
            if (lastHour > max)
            {
                Reply(dv, conv.Id, "You've reached the limit of " + max + " messages an hour in this chat. Please try again a little later, or contact the DealOS team.");
                return;
            }

            var def = PickAgent(dv, conv, account.Id);
            var run = AgentHost.Run(def, dv, trace, conv.Id, J.Obj("message_id", target.Id.ToString(), "trigger", "chat:gc_message"), false, "chat:gc_message");
            if (run.Outcome.Status != "Succeeded")
            {
                trace.Trace("Chat agent {0} failed: {1}", def.ApiName, run.Outcome.Error);
                Reply(dv, conv.Id, "Sorry, I couldn't answer that just now. Please try again in a minute; a DealOS team member can also help.");
            }
        }

        /// <summary>The conversation's assistant; when unset, Seller Assistant for seller-only accounts, otherwise Buyer Concierge.</summary>
        private static AgentDefinition PickAgent(Dv dv, Entity conv, Guid accountId)
        {
            var assistant = conv.GetAttributeValue<OptionSetValue>("gc_assistant");
            var seller = AgentCatalog.All.OfType<SellerAssistantAgent>().First();
            var buyer = AgentCatalog.All.OfType<BuyerConciergeAgent>().First();
            if (assistant != null) return assistant.Value == Choice.Base + 1 ? (AgentDefinition)seller : buyer;
            var acc = dv.System.Retrieve("account", accountId, new ColumnSet("gc_partyrole"));
            var roles = acc.GetAttributeValue<OptionSetValueCollection>("gc_partyrole");
            var isSeller = roles != null && roles.Any(r => r.Value == Choice.Base);
            var isBuyer = roles != null && roles.Any(r => r.Value == Choice.Base + 1);
            return isSeller && !isBuyer ? (AgentDefinition)seller : buyer;
        }

        private static bool BelongsTo(Dv dv, Guid contactId, Guid accountId)
        {
            var c = dv.System.RetrieveMultiple(new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("parentcustomerid"),
                Criteria = { Conditions = { new ConditionExpression("contactid", ConditionOperator.Equal, contactId) } }
            }).Entities.FirstOrDefault();
            var parent = c == null ? null : c.GetAttributeValue<EntityReference>("parentcustomerid");
            return parent != null && parent.Id == accountId;
        }

        private static void Reply(Dv dv, Guid conversationId, string text)
        {
            var msg = new Entity("gc_message");
            msg["gc_name"] = GeminiClient.Truncate("DealOS: " + text, 100);
            msg["gc_conversation"] = new EntityReference("gc_conversation", conversationId);
            msg["gc_isplatform"] = true;
            msg["gc_senderlabel"] = "DealOS";
            msg["gc_text"] = text;
            msg["gc_senton"] = DateTime.UtcNow;
            dv.System.Create(msg);
        }
    }
}
