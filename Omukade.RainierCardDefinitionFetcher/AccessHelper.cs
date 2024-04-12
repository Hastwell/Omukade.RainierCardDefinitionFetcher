/*************************************************************************
* Rainier Card Definition Fetcher
* (c) 2022 Hastwell/Electrosheep Networks 
* 
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU Affero General Public License as published
* by the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
* 
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU Affero General Public License for more details.
* 
* You should have received a copy of the GNU Affero General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
**************************************************************************/

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TPCI.PTCS;
using static Omukade.Tools.RainierCardDefinitionFetcher.Manipulators;

namespace Omukade.Tools.RainierCardDefinitionFetcher
{
    internal static class AccessHelper
    {
        public static PTCSUtils.TokenData GetTokenForUsernameAndPassword(string username, string password)
        {
            TPCI.PTCS.ClientData clientData = newWithoutConstructor<TPCI.PTCS.ClientData>();
            clientData.clientID = "tpci-tcg-app";
            clientData.redirectURI = "https://tpcitcgapp/callback";
            clientData.scope = new string[] { "offline", "screen_name", "openid", "friends" };

            const string AUTH_STAGE_1_PREFIX = "https://access.pokemon.com/oauth2/auth";
            const string AUDIENCE_VALUE = "https://op-core.pokemon.com+https://api.friends.pokemon.com";
            const string SELECTED_LANGUAGE = "en";
            string stage1url = TPCI.PTCS.PTCSUtils.GetAuthRequest(AUTH_STAGE_1_PREFIX, AUDIENCE_VALUE, clientData, SELECTED_LANGUAGE, out string challenge, out string verifier);

            HttpClient httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            httpClient.DefaultRequestHeaders.Add("User-Agent", @"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0");

            // Stage 1 - GET /oauth2/auth
            HttpResponseMessage stage1result = httpClient.GetAsync(stage1url).Result;
            string stage1payload = stage1result.Content.ReadAsStringAsync().Result;

            // Stage 2 - GET /login?login_challenge
            string stage2loginUrl = Regex.Match(stage1payload, @"https://access.pokemon.com/login\?login_challenge=\w+").Value;
            //string stage2csrfHeader = stage1result.Headers.GetValues("Set-Cookie").First(header => header.StartsWith("oauth2_authentication_csrf"));

            HttpResponseMessage stage2result = httpClient.GetAsync(stage2loginUrl).Result;
            string stage2payload = stage2result.Content.ReadAsStringAsync().Result;

            Dictionary<string, string> stage2hiddenFields = Regex.Matches(stage2payload, @"<input type=""hidden"" name=""(_csrf|challenge)"" value=""([^""]+)"">")
                .ToDictionary(k => k.Groups[1].Value, v => v.Groups[2].Value);

            // Stage 3 - POST /login
            //string stage3postBody = $"_csrf={stage2hiddenFields["_csrf"]}&challenge={stage2hiddenFields["challenge"]}&email={USERNAME}&password={System.Web.HttpUtility.UrlEncode(PASSWORD)}";
            FormUrlEncodedContent stage3postbody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                {"_csrf", stage2hiddenFields["_csrf"] },
                {"challenge", stage2hiddenFields["challenge"] },
                {"email", username },
                {"password", password },
            });

            HttpResponseMessage stage3result = httpClient.PostAsync("https://access.pokemon.com/login", stage3postbody).Result;
            string stage3payload = stage3result.Content.ReadAsStringAsync().Result;

            if(stage3payload.Contains("Pardon Our Interruption"))
            {
                throw new Exception("Detected as bot during stage 3 call");
            }

            // Stage 4 - GET /oauth2/auth
            Uri? stage4url = stage3result.Headers.Location;
            HttpResponseMessage stage4result = httpClient.GetAsync(stage4url).Result;

            // some affirmation prompt would appear here if consent was not already given

            // Stage 5 - GET /consent
            Uri? stage5url = stage4result.Headers.Location;
            HttpResponseMessage stage5result = httpClient.GetAsync(stage5url).Result;

            // Stage 6 - GET /oauth2/auth
            Uri? stage6rawlocation = stage5result.Headers.Location;
            HttpResponseMessage stage6result = httpClient.GetAsync(stage6rawlocation).Result;

            // Stage 7 - POST /oauth2/token
            System.Collections.Specialized.NameValueCollection stage7queryparams = System.Web.HttpUtility.ParseQueryString(stage6result.Headers.Location.Query.TrimStart('?'));
            FormUrlEncodedContent stage6postbody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                {"client_id", clientData.clientID },
                {"code", stage7queryparams["code"] },
                {"code_verifier", verifier },
                {"grant_type", "authorization_code" },
                {"redirect_uri", clientData.redirectURI},
                {"state", challenge }
            });

            HttpResponseMessage stage7result = httpClient.PostAsync("https://access.pokemon.com/oauth2/token", stage6postbody).Result;
            string stage7body = stage7result.Content.ReadAsStringAsync().Result;

            return JsonConvert.DeserializeObject<PTCSUtils.TokenData>(stage7body);
        }
    }
}
