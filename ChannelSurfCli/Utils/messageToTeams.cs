using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using ChannelSurfCli.Models;

namespace ChannelSurfCli.Utils
{
    public class messagesToTeams
    {
        public string postMessage(string aadAccessToken, string teamID, string channelID, string bodyContent)
        {
            Helpers.httpClient.DefaultRequestHeaders.Clear();
            Helpers.httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aadAccessToken);
            Helpers.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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
          
            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                Console.WriteLine("ERROR: Message Not Posted");
                Console.WriteLine("REASON: " + httpResponseMessage.Content.ReadAsStringAsync().Result);
                return "";
            }



            return "1";
        }
    }
}