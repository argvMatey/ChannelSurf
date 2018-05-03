using ChannelSurfCli.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Graph;


namespace ChannelSurfCli.Utils
{
    public class Messages
    {
        

        public static void ScanMessagesByChannel(List<Models.Combined.ChannelsMapping> channelsMapping, string basePath,
            List<ViewModels.SimpleUser> slackUserList, String aadAccessToken, String selectedTeamId, bool copyFileAttachments)
        {
            
            foreach (var v in channelsMapping)
            {
                var channelAttachmentsToUpload = GetAndUploadMessages(v, basePath, slackUserList, aadAccessToken, selectedTeamId, copyFileAttachments);
            }

            return;
        }


        static List<Models.Combined.AttachmentsMapping> GetAndUploadMessages(Models.Combined.ChannelsMapping channelsMapping, string basePath,
            List<ViewModels.SimpleUser> slackUserList, String aadAccessToken, String selectedTeamId, bool copyFileAttachments)
        {
            var messageList = new List<ViewModels.SimpleMessage>();
            messageList.Clear();
            
            var messageListJsonSource = new JArray();
            messageListJsonSource.Clear();

            List<Models.Combined.AttachmentsMapping> attachmentsToUpload = new List<Models.Combined.AttachmentsMapping>();
            attachmentsToUpload.Clear();

            Console.WriteLine("Migrating messages in channel " + channelsMapping.slackChannelName);
            foreach (var file in Directory.GetFiles(Path.Combine(basePath, channelsMapping.slackChannelName)))
            {
                Console.WriteLine("File " + file);
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (StreamReader sr = new StreamReader(fs))
                using (JsonTextReader reader = new JsonTextReader(sr))
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.StartObject)
                        {
                            JObject obj = JObject.Load(reader);

                            // SelectToken returns null not an empty string if nothing is found
                            // I'm too lazy right now for strongly typed classes

                            // deal with message basics: when, body, who

                            var messageTs = (string)obj.SelectToken("ts");

                            //added 04/18/2018 to make timestamps friendlier
                            // 04/25/2018 try to convert to local time from UTC
                            if (messageTs != null)
                            {
                                System.DateTime dateTime = new System.DateTime(1970, 1, 1, 0, 0, 0, 0);
                                dateTime = dateTime.AddSeconds(Convert.ToDouble(messageTs));
                                dateTime = dateTime.ToLocalTime();
                                messageTs = Convert.ToString(dateTime);
                                
                            }
                            
                            var messageText = (string)obj.SelectToken("text");
                            var messageId = channelsMapping.slackChannelId + "." + messageTs;

                            
                             
                              /// for regex matching of in-message usernames

                               //Multiple users?
                            Match match = Regex.Match(messageText, @"\<\@([A-Za-z0-9\-]+)\>", RegexOptions.IgnoreCase);
                            while (match.Success)
                           {
                                string inMsgSlackUserId = match.Value.Replace("<", "").Replace("@", "").Replace(">", "");
                                messageText = Regex.Replace(messageText, @"\<\@([A-Za-z0-9\-]+)\>", findInMsgUser(inMsgSlackUserId, slackUserList));
                                match = Regex.Match(messageText, @"\<\@([A-Za-z0-9\-]+)\>", RegexOptions.IgnoreCase);
                            }
                            // A better, more object oriented implementaion would be better
                            //messageText = RegexDetector.DetectSlackParens(messageText, slackUserList);

                            var messageSender = Utils.Messages.FindMessageSender(obj, slackUserList);

                            // create a list of attachments to upload
                            // deal with "attachments" that are files
                            // specifically, files hosted by Slack

                            // SelectToken returns null not an empty string if nothing is found
                            var fileUrl = (string)obj.SelectToken("file.url_private");
                            var fileId = (string)obj.SelectToken("file.id");
                            var fileMode = (string)obj.SelectToken("file.mode");
                            var fileName = (string)obj.SelectToken("file.name");

                            // sometimes, slack returns a filename without an extension, which makes it a pain to 
                            //open the attachment.  what's a reliable way to check file extensions? what field will give a clean reference?
                            if (fileName != null)
                            {
                                if (fileName.Contains("_"))
                                {
                                    fileName = ReplaceLastOccurence(fileName, "_", ".");
                                }
                            }
                            //^^I'm just replacing the last occurence of _ with . , because hosted files seem to be in that format
                            //Let's see if it works - so far, so good 

                            ViewModels.SimpleMessage.FileAttachment fileAttachment = null;

                            if (fileMode != "external" && fileId != null && fileUrl != null)
                            {
                                Console.WriteLine("Message attachment found with ID " + fileId);
                                attachmentsToUpload.Add(new Models.Combined.AttachmentsMapping
                                {
                                    attachmentId = fileId,
                                    attachmentUrl = fileUrl,
                                    attachmentChannelId = channelsMapping.slackChannelId,
                                    attachmentFileName = fileName,
                                    msChannelName = channelsMapping.displayName
                                });

                                // map the attachment to fileAttachment which is used in the viewmodel

                                fileAttachment = new ViewModels.SimpleMessage.FileAttachment
                                {
                                    id = fileId,
                                    originalName = (string)obj.SelectToken("file.name"),
                                    originalTitle = (string)obj.SelectToken("file.title"),
                                    originalUrl = (string)obj.SelectToken("file.permalink")
                                };
                            }

                            // deal with "attachments" that aren't files

                            List<ViewModels.SimpleMessage.Attachments> attachmentsList = new List<ViewModels.SimpleMessage.Attachments>();
                            List<ViewModels.SimpleMessage.Attachments.Fields> fieldsList = new List<ViewModels.SimpleMessage.Attachments.Fields>();

//added try catch to handle the case of "attachments"": null , which was causing this to fail
                            try
                            {
                                var attachmentsObject = (JArray)obj.SelectToken("attachments");

                                if (attachmentsObject != null)
                                {

                                    foreach (var attachmentItem in attachmentsObject)
                                    {
                                        var attachmentText = (string)attachmentItem.SelectToken("text");
                                        var attachmentTextFallback = (string)attachmentItem.SelectToken("fallback");

                                        var attachmentItemToAdd = new ViewModels.SimpleMessage.Attachments();

                                        if (!String.IsNullOrEmpty(attachmentText))
                                        {
                                            attachmentItemToAdd.text = attachmentText;
                                        }
                                        else if (!String.IsNullOrEmpty(attachmentTextFallback))
                                        {
                                            attachmentItemToAdd.text = attachmentTextFallback;
                                        }

                                        var attachmentServiceName = (string)attachmentItem.SelectToken("service_name");
                                        if (!String.IsNullOrEmpty(attachmentServiceName))
                                        {
                                            attachmentItemToAdd.service_name = attachmentServiceName;
                                        }

                                        var attachmentFromUrl = (string)attachmentItem.SelectToken("from_url");
                                        if (!String.IsNullOrEmpty(attachmentFromUrl))
                                        {
                                            attachmentItemToAdd.url = attachmentFromUrl;
                                        }

                                        var attachmentColor = (string)attachmentItem.SelectToken("color");
                                        if (!String.IsNullOrEmpty(attachmentColor))
                                        {
                                            attachmentItemToAdd.color = attachmentColor;
                                        }

                                        var fieldsObject = (JArray)attachmentItem.SelectToken("fields");
                                        if (fieldsObject != null)
                                        {
                                            fieldsList.Clear();
                                            foreach (var fieldItem in fieldsObject)
                                            {
                                                fieldsList.Add(new ViewModels.SimpleMessage.Attachments.Fields()
                                                {
                                                    title = (string)fieldItem.SelectToken("title"),
                                                    value = (string)fieldItem.SelectToken("value"),
                                                    shortWidth = (bool)fieldItem.SelectToken("short")
                                                });
                                            }
                                            attachmentItemToAdd.fields = fieldsList;
                                        }
                                        else
                                        {
                                            attachmentItemToAdd.fields = null;
                                        }
                                        attachmentsList.Add(attachmentItemToAdd);
                                    }
                                }
                                else
                                {
                                    attachmentsList = null;
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e.Message);
                                attachmentsList = null;
                            }


                            // do some stuff with slack message threading at some point

                            messageList.Add(new ViewModels.SimpleMessage
                            {
                                id = messageId,
                                text = messageText,
                                ts = messageTs,
                                user = messageSender,
                                fileAttachment = fileAttachment,
                                attachments = attachmentsList,
                            });
                        }

                    }
                }
            }

            if(copyFileAttachments)
            {
                Utils.FileAttachments.ArchiveMessageFileAttachments(aadAccessToken,selectedTeamId,attachmentsToUpload,"fileattachments").Wait();

                foreach(var messageItem in messageList)
                {
                    if(messageItem.fileAttachment != null)
                    {
                        var messageItemWithFileAttachment = attachmentsToUpload.Find(w => String.Equals(messageItem.fileAttachment.id,w.attachmentId,StringComparison.CurrentCultureIgnoreCase));
                        if(messageItemWithFileAttachment != null)
                        {
                            messageItem.fileAttachment.spoId = messageItemWithFileAttachment.msSpoId;
                            messageItem.fileAttachment.spoUrl= messageItemWithFileAttachment.msSpoUrl;
                        }
                    }
                }
            }
            //Utils.Messages.CreateSlackMessageJsonArchiveFile(basePath, channelsMapping, messageList, aadAccessToken, selectedTeamId);
            Utils.Messages.CreateSlackMessageHtmlArchiveFile(basePath, channelsMapping, messageList, aadAccessToken, selectedTeamId);

            return attachmentsToUpload;
        }

        static void CreateSlackMessageJsonArchiveFile(String basePath, Models.Combined.ChannelsMapping channelsMapping, List<ViewModels.SimpleMessage> messageList,
            String aadAccessToken, string selectedTeamId)
        {
            int messageIndexPosition = 0;

            for (int slackMessageFileIndex = 0; messageIndexPosition < messageList.Count; slackMessageFileIndex++)
            {
                var filenameToAdd = slackMessageFileIndex.ToString() + ".json";
                using (FileStream fs = new FileStream(Path.Combine(basePath, channelsMapping.slackChannelName, slackMessageFileIndex.ToString() + ".json"), FileMode.Create))
                {
                    using (StreamWriter w = new StreamWriter(fs, Encoding.UTF8))
                    {
                        int numOfMessagesToTake = 0;
                        /*
                        if (messageIndexPosition + 250 <= messageList.Count)
                        {
                            numOfMessagesToTake = 250;
                        }
                        else
                        {*/
                            numOfMessagesToTake = messageList.Count - messageIndexPosition;
                        //}
                        var jsonObjectsToSave = JsonConvert.SerializeObject(messageList.Skip(messageIndexPosition).Take(numOfMessagesToTake), Formatting.Indented);
                        messageIndexPosition += numOfMessagesToTake;
                        w.WriteLine(jsonObjectsToSave);
                    }
                }
                var pathToItem = "/" + channelsMapping.displayName + "/channelsurf/" + "messages/json" + "/" + filenameToAdd;
                Utils.FileAttachments.UploadFileToTeamsChannel(aadAccessToken, selectedTeamId, Path.Combine(basePath, channelsMapping.slackChannelName, filenameToAdd), pathToItem).Wait();
            }
            return;
        }

        static void CreateSlackMessageHtmlArchiveFile(String basePath, Models.Combined.ChannelsMapping channelsMapping, List<ViewModels.SimpleMessage> messageList,
            String aadAccessToken, string selectedTeamId)
        {
            int messageIndexPosition = 0;

            for (int slackMessageFileIndex = 0; messageIndexPosition < messageList.Count; slackMessageFileIndex++)
            {
                var filenameToAdd = slackMessageFileIndex.ToString() + ".html";
                using (FileStream fs = new FileStream(Path.Combine(basePath, channelsMapping.slackChannelName, slackMessageFileIndex.ToString() + ".html"), FileMode.Create))
                {
                    using (StreamWriter w = new StreamWriter(fs, Encoding.UTF8))
                    {
                        int numOfMessagesToTake = 0;
                        int messageBatchSize = 20;

                        //if (messageIndexPosition + 20 <= messageList.Count)
                        //{
                        //    numOfMessagesToTake = 20;
                        //}
                        //else
                        //{
                            numOfMessagesToTake = messageList.Count - messageIndexPosition;
                        //}
                        StringBuilder fileBody = new StringBuilder();
                        fileBody.Append("<body>");
                        fileBody.AppendLine("");
                        ;

                        Random r = new Random();
                        int rInt;

                        dynamic wrapper = new JObject();
                        JArray requests = new JArray();

                        for (int i = 0; i < numOfMessagesToTake; i++)
                        {
                            var messageAsHtml = MessageToHtml(messageList[messageIndexPosition + i], channelsMapping);
                            fileBody.AppendLine(messageAsHtml);

                            rInt = r.Next(700, 1300);
                            System.Threading.Thread.Sleep(rInt);
                            string check = postMessage(aadAccessToken, selectedTeamId, channelsMapping.id, messageAsHtml);
                            if(check != "1") 
                            {
                                aadAccessToken = check; 
                            }

                        }


                        //for (int i = 0; i < messageBatchSize; i++)
                        //{
                        //    var messageAsHtml = MessageToHtml(messageList[messageIndexPosition + i], channelsMapping);
                        //    //hitting activity limit. cannot batch requests added random 100 - 1000 ms delay here, and exponential backoff in postMessage on failure
                        //    requests.Add(buildMessageBatch(i + 1, selectedTeamId, channelsMapping.id, messageAsHtml));

                        //    //
                        //}

                        ////trying to post a batch of messages - what is the upper limit?
                        //wrapper.requests = requests;
                        //rInt = r.Next(100, 500);
                        //System.Threading.Thread.Sleep(rInt);
                        //postMessageBatch(aadAccessToken, wrapper);

                        fileBody.AppendLine("</body>");
                        messageIndexPosition += numOfMessagesToTake;
                        w.WriteLine(fileBody);
                    }
                }
               

                var pathToItem = "/" + channelsMapping.displayName + "/slackArchive/" + filenameToAdd;
                Utils.FileAttachments.UploadFileToTeamsChannel(aadAccessToken, selectedTeamId, Path.Combine(basePath, channelsMapping.slackChannelName, filenameToAdd), pathToItem).Wait();
            }

            return;
        }

        // this is ugly and should/will eventually be replaced by its own class

        public static string MessageToHtml(ViewModels.SimpleMessage simpleMessage, Models.Combined.ChannelsMapping channelsMapping)
        {
            string w = "";
            w += "<div>";
            w += ("<div id=\"" + simpleMessage.id + "\">");
            w += ("<span id=\"user_id\" style=\"font-weight:bold;\">" + simpleMessage.user + "</span>");
            w += ("&nbsp;");
            w += ("<span id=\"epoch_time\" style=\"font-weight:lighter;\">" + simpleMessage.ts + "</span>");
            w += ("<br/>");
            w += ("<div id=\"message_text\" style=\"font-weight:normal;white-space:pre-wrap;\">" + simpleMessage.text + "</div>");

            if (simpleMessage.fileAttachment != null)
            {
                w += "<div style=\"margin-left:1%;margin-top:1%;border-left-style:solid;border-left-color:LightGrey;\">";
                w += "<div style=\"margin-left:1%;\">";
                if(simpleMessage.fileAttachment.spoId != null)
                {
                    w += "<span style=\"font-weight:lighter;\"> <a href=\"" + simpleMessage.fileAttachment.spoUrl + "\"> File Attachment </a> </span>";
                }
                w += "<div>";
                w += "<span style=\"font-weight:lighter;\"> ";
                w += simpleMessage.fileAttachment.originalTitle + "<br/>";
                w += simpleMessage.fileAttachment.originalUrl + " <br/>";
                w += "</span>";
                w += "</div>";
                w += "</div>";
                w += "</div>";
            }
            if (simpleMessage.attachments != null)
            {

                foreach (var attachment in simpleMessage.attachments)
                {
                    w += "<div style=\"margin-left:1%;margin-top:1%;border-left-style:solid;border-left-color:";
                    if (!String.IsNullOrEmpty(attachment.color))
                    {
                        w += "#" + attachment.color + ";";
                    }
                    else
                    {
                        w += "LightGrey;";
                    }
                    w += "\">";
                    w += "<div style=\"margin-left:1%;\">";
                    if (!String.IsNullOrEmpty(attachment.service_name))
                    {
                        w += "<span style=\"font-weight:bolder;\">" + attachment.service_name + "</span><br/>";
                    }
                    w += "<div style=\"font-weight:lighter;white-space:pre-wrap;\">" + attachment.text + "</div>";
                    w += "<a style=\"font-weight:lighter;\" href=\"" + attachment.url + "\">" + attachment.url + "</a><br/>";
                    if (attachment.fields != null)
                    {
                        if (attachment.fields.Count > 0)
                        {
                            w += "<table class=\"\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">";

                            foreach (var field in attachment.fields)
                            {
                                if (true) 
                                {
                                    w += "<tr><td>";
                                    w += "<div>" + field.title + "</div>";
                                    w += "<div>" + field.value + "</div>";
                                    w += "</tr></td>";
                                }
                            }
                            w += "</table>";
                        }
                    }
                    w += "</div>";
                    w += "</div>";
                }
            }
            w += "</div>";
            w += "<p/>";
            w += "</div>";
            return w;
        }

        static string FindMessageSender(JObject obj, List<ViewModels.SimpleUser> slackUserList)
        {
            var user = (string)obj.SelectToken("user");
            if (!String.IsNullOrEmpty(user))
            {
                if (user != "USLACKBOT")
                {
                    var simpleUser = slackUserList.FirstOrDefault(w => w.userId == user);
                    if (simpleUser != null)
                    {
                        return simpleUser.name;
                    }

                }
                else
                {
                    return "SlackBot";
                }
            }
            else if (!(String.IsNullOrEmpty((string)obj.SelectToken("username"))))
            {
                return (string)obj.SelectToken("username");
            }
            else if (!(String.IsNullOrEmpty((string)obj.SelectToken("bot_id"))))
            {
                return (string)obj.SelectToken("bot_id");
            }

            return "";
        }

        static string findInMsgUser(string user, List<ViewModels.SimpleUser> slackUserList)
        {            
            if (!String.IsNullOrEmpty(user))
            {
                if (user != "U00|")
                {
                    var simpleUser = slackUserList.FirstOrDefault(w => w.userId == user);
                    if (simpleUser != null)
                    {
                        return simpleUser.name;
                    }

                }
                else
                {
                    return "SlackBot";
                }
            }
            return "";
        }

        public static string ReplaceLastOccurence(string originalValue, string occurenceValue, string newValue)
        {
            if (string.IsNullOrEmpty(originalValue))
                return originalValue;
            if (string.IsNullOrEmpty(occurenceValue))
                return originalValue;
            if (string.IsNullOrEmpty(newValue))
                return originalValue;
            int startindex = originalValue.LastIndexOf(occurenceValue);
            return originalValue.Remove(startindex, occurenceValue.Length).Insert(startindex, newValue);
        }

        public static string postMessage(string aadAccessToken, string teamID, string channelID, string bodyContent)
        {
            
            int sleeper = 1;


            // this might break on some platforms
            dynamic messageBody = new JObject();
            dynamic newMessage = new JObject();
            dynamic rootMessage = new JObject();

            messageBody.contentType = 2;
            messageBody.content = bodyContent;
            rootMessage.body = messageBody;
            newMessage.rootMessage = rootMessage;

            var createMsGroupPostData = JsonConvert.SerializeObject(newMessage);
            var httpResponseMessage =
                Helpers.httpClient.PostAsync(O365.MsGraphBetaEndpoint + "groups/" + teamID + "/Channels/" + channelID + "/chatThreads",
                    new StringContent(createMsGroupPostData, Encoding.UTF8, "application/json")).Result;
            Console.WriteLine(httpResponseMessage.ReasonPhrase);
            Console.WriteLine(httpResponseMessage.Headers);

            //need to back off the requests if tooManyRequests 
            while (!httpResponseMessage.IsSuccessStatusCode)
            {
                    Console.WriteLine(httpResponseMessage.ReasonPhrase);
                    Console.WriteLine("ERROR: Message Not Posted");
                Console.WriteLine("REASON: " + httpResponseMessage.Content.ReadAsStringAsync().Result);

                JObject j = JObject.Parse(httpResponseMessage.Content.ReadAsStringAsync().Result);
                Console.WriteLine(j["error"]["code"].ToString());
                if(j["error"]["code"].ToString() == "ActivityLimitReached")
                {
                    sleeper = sleeper * 2;
                }
                if (j["error"]["code"].ToString() == "InvalidAuthenticationToken")
                {
                    // need to re-authenticate when token expires - made authenticationcontext public static 
                    //tried to set auth token expiry to 23 hours - still not working
                    // the below returns something that looks like a token
                    //this piece is successfully re-authenticating 
                    //need to test if the new access token gets passed to the rest of the functions - upload file attachments and html file
                    string newToken = Program.authenticationContext.AcquireTokenSilentAsync(Program.aadResourceAppId, Program.Configuration["AzureAd:ClientId"]).Result.AccessToken;

                    Helpers.httpClient.DefaultRequestHeaders.Clear();
                    Helpers.httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                    Helpers.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    sleeper = 1;

                    Console.WriteLine("Retrying Message Post in " + Convert.ToString(sleeper) + " seconds");
                    System.Threading.Thread.Sleep(sleeper * 1000);
                    httpResponseMessage =
                    Helpers.httpClient.PostAsync(O365.MsGraphBetaEndpoint + "groups/" + teamID + "/Channels/" + channelID + "/chatThreads",
                        new StringContent(createMsGroupPostData, Encoding.UTF8, "application/json")).Result;

                    return newToken;
                }

                    Console.WriteLine("Retrying Message Post in " + Convert.ToString(sleeper) + " seconds");  
                    System.Threading.Thread.Sleep(sleeper * 1000);
                    httpResponseMessage =
                    Helpers.httpClient.PostAsync(O365.MsGraphBetaEndpoint + "groups/" + teamID + "/Channels/" + channelID + "/chatThreads",
                        new StringContent(createMsGroupPostData, Encoding.UTF8, "application/json")).Result;
                    
            }

            return "1";
        }

        //developing to better deal with throttling - runs into activitylimit before posting a single message does.
        public static string postMessageBatch(string aadAccessToken, JObject wrapper)
        {
            int i = 2;
            int sleeper = 1;
            Helpers.httpClient.DefaultRequestHeaders.Clear();
            Helpers.httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aadAccessToken);
            Helpers.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var createMsGroupPostData = JsonConvert.SerializeObject(wrapper);
            //Console.WriteLine(createMsGroupPostData);

            var httpResponseMessage =
                Helpers.httpClient.PostAsync(O365.MsGraphBetaEndpoint + "$batch",
                    new StringContent(createMsGroupPostData, Encoding.UTF8, "application/json")).Result;

            String content = httpResponseMessage.Content.ReadAsStringAsync().Result;
            JObject responseData = JObject.Parse(content);
            JArray a = JArray.Parse(content);
            string responses = JsonConvert.SerializeObject(responseData);


            //var responses = responseData["responses"];
            //var response = responses.Children();
            //Console.WriteLine(response[0]);



            System.Threading.Thread.Sleep(5000);




                // batching requests does not throw an error code in this manner. Begin the Whackamole
                //need to back off the requests if tooManyRequests 
                while (!httpResponseMessage.IsSuccessStatusCode)
                {
                    Console.WriteLine("ERROR: Message Not Posted");
                    Console.WriteLine("REASON: " + httpResponseMessage.Content.ReadAsStringAsync().Result);
                    Console.WriteLine("Retrying Message Post");
                    System.Threading.Thread.Sleep(sleeper * 1000);

                    httpResponseMessage =
                    Helpers.httpClient.PostAsync(O365.MsGraphBetaEndpoint + "$batch",
                        new StringContent(createMsGroupPostData, Encoding.UTF8, "application/json")).Result;
                    sleeper = sleeper * 2;
                }

               

            return "1";
        }

        public static JObject buildMessageBatch(int i, string teamID, string channelID, string bodyContent)
        {
         

            // this might break on some platforms
            dynamic messageBody = new JObject();
            dynamic newMessage = new JObject();
            dynamic rootMessage = new JObject();
            
            dynamic request = new JObject();
            dynamic dependsOn = new JArray();
            dynamic headers = new JObject();
            dynamic requestBody = new JObject();
            
            messageBody.contentType = 2;
            messageBody.content = bodyContent;
            rootMessage.body = messageBody;
            requestBody.rootMessage = rootMessage;

            headers["Content-Type"] = "application/json";

            request.headers = headers;
            request.body = requestBody;
            request.id = Convert.ToString(i);
            if (i != 1)
            {
                dependsOn.Add(Convert.ToString(i - 1));
                request.dependsOn = dependsOn;
            }
            request.method = "POST";
            request.url = "/groups/" + teamID + "/Channels/" + channelID + "/chatThreads";


            ///requests.Add(request);
            //wrapper.requests = requests;

            //var createMsGroupPostData = JsonConvert.SerializeObject(wrapper);
            //Console.WriteLine(createMsGroupPostData);

            return request;
        }

   

    }

}


