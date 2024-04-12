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

using CardDatabase.DataAccess;
using MatchLogic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Omukade.Tools.RainierCardDefinitionFetcher.Model;
using ClientNetworking;
using ClientNetworking.Models.Config;
using ClientNetworking.Models.Friend;
using ClientNetworking.Models.Query;
using SharedLogicUtils.Services.Query.Contexts;
using SharedLogicUtils.Services.Query.Responses;
using SharedLogicUtils.source.CardData;
using SharedLogicUtils.source.Services.Query.Contexts;
using SharedLogicUtils.source.Services.Query.Responses;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static TPCI.PTCS.PTCSUtils;
using ClientNetworking.Models.Account;

namespace Omukade.Tools.RainierCardDefinitionFetcher
{
    public static class Fetchers
    {
        const string OUTPUT_FOLDER_CARD_DEFINITIONS = @"PTCGL-CardDefinitions";
        const string OUTPUT_FOLDER_CARD_DATABASE = @"PTCGL-CardDatabase";
        const string OUTPUT_FOLDER_CARD_ACTIONS = @"PTCGL-CardActions";
        const string OUTPUT_FOLDER_QUEST_DATA = @"PTCGL-QuestData";
        const string OUTPUT_FOLDER_DECKVALIDATION_DATA = @"PTCGL-DeckValidation";
        const string OMUKADE_FAKE_BOARD_ID = "OMUKADE-FAKE-BOARD-ID";
        const string OMUKADE_FAKE_OPPONENT_ID = "OMUKADE-FAKE-OPPONENT-ID";

        public static Client PrepareClient(SecretsConfig secrets)
        {
            TokenData tokenData = AccessHelper.GetTokenForUsernameAndPassword(secrets.username, secrets.password);

            // Access Key is hardcoded in Client.Setup
            const string ACCESS_KEY = "421d8904-0236-4ab4-94f5-a8a84aeb3f7b";

            // DEVICE_ID is derived from, in preference:
            // * UnityEngine.SystemInfo.deviceUniqueIdentifier
            // * PlayerPrefs.GetString("GameVersionInfo:Identifier")
            // * Guid.NewGuid()
            string DEVICE_ID = Guid.NewGuid().ToString(); //"1047b8069bcaa0358004cb88aad57f5cc7dc4759";

            // CLIENT_ID is derived from, in perference:
            // * tokenData.id_token
            // * If PlayerPrefs.GetInt("prefs-random-user") > 0, Guid.NewGuid()
            // * UnityEngine.SystemInfo.deviceUniqueIdentifier
            // * PlayerPrefs.GetString("GameVersionInfo:Identifier")
            // * Guid.NewGuid()
            string CLIENT_ID = tokenData.id_token ?? Guid.NewGuid().ToString(); // "6a9d54403b2ba18414990995b57b2632";

            Client client = new ClientBuilder()
                .WithStage(Stages.PROD)
                .WithAccessKey(ACCESS_KEY)
                .WithDeviceInfo(deviceId: DEVICE_ID, deviceType: null, "Windows")
                .Create(clientId: CLIENT_ID);

            client.RegisterAsync().Wait();
            client.MakeSyncCall<string, string, TokenResponse>(client.AuthAsync, tokenData.access_token, "PJWT");

            return client;
        }

        private static string GetOutputFolder()
        {
            if(Program.outputFolder != null) return Program.outputFolder;

            return Omukade.AutoPAR.Rainier.RainierSharedDataHelper.GetSharedDataDirectory();
        }

        internal static void FetchAndSaveCardDefinitionsInteractive(Client client, bool leveragePreviousInvalidCardIds = false)
        {
            if (Program.quietFlagEnabled)
            {
                CardDefinitionFetchCore(ctx: null, client, leveragePreviousInvalidCardIds);
            }
            else
            {
                AnsiConsole.Progress()
                    .HideCompleted(false)
                    .AutoClear(false)
                    .Start(ctx => CardDefinitionFetchCore(ctx, client, leveragePreviousInvalidCardIds));
            }
        }

        public static void FetchAndSaveCardDefinitions(Client client, bool leveragePreviousInvalidCardIds = false)
        {
            CardDefinitionFetchCore(ctx: null, client, leveragePreviousInvalidCardIds);
        }

        private static void CardDefinitionFetchCore(ProgressContext? ctx, Client client, bool leveragePreviousInvalidCardIds)
        {
            ConfigDocumentGetResponse setNameManifestDocument = client.GetConfigDocumentSync("set-manifest_0.0");
            SetNamesManifest setNameManifest = JsonConvert.DeserializeObject<SetNamesManifest>(setNameManifestDocument.data["manifest"].contentString)!;

            IEnumerable<ConfigDocumentGetRequest> docRequests = setNameManifest.sets.Select(set => new ConfigDocumentGetRequest(set + "-compendium_0.0"));
            ConfigDocumentGetMultipleResponse cgmr = client.MakeSyncCall<IEnumerable<ConfigDocumentGetRequest>, ConfigDocumentGetMultipleResponse>(client.GetConfigDocumentsAsync, docRequests);

            string INVALID_CARD_IDS_FILE = Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DEFINITIONS, "invalid-card-ids.txt");
            List<string> invalidCardIds;
            HashSet<string> previousInvalidCardIds;
            if(leveragePreviousInvalidCardIds && File.Exists(INVALID_CARD_IDS_FILE))
            {
                previousInvalidCardIds = new HashSet<string>(File.ReadAllLines(INVALID_CARD_IDS_FILE));
                invalidCardIds = previousInvalidCardIds.ToList();
            }
            else
            {
                previousInvalidCardIds = new HashSet<string>();
                invalidCardIds = new List<string>();
            }

            Dictionary<string, Guid> getCompendiumData(ConfigDocumentGetResponse doc) => JsonConvert.DeserializeObject<Dictionary<string, Guid>>(doc.data["compendium"].contentString)!;

            ProgressTask? fetchTask = null;
            if (ctx != null)
            {
                fetchTask = ctx.AddTask("Fetch card definitions...");
                fetchTask.MaxValue = cgmr.documents.Values.Select(doc => getCompendiumData(doc).Count).Sum();
            }

            foreach (ConfigDocumentGetResponse doc in cgmr.documents.Values)
            {
                Dictionary<string, Guid> cardIds = JsonConvert.DeserializeObject<Dictionary<string, Guid>>(doc.data["compendium"].contentString)!;

                if (cardIds.Count == 0)
                {
                    continue;
                }

                string setName = doc.id.Replace("-compendium_0.0", "");
                if (fetchTask != null) fetchTask.Description = $"Fetch card definitions ({setName})";

                string setFolder = Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DEFINITIONS, setName);
                Directory.CreateDirectory(setFolder);

                var cardIdData = new { cardIDs = (IEnumerable<string>)cardIds.Keys.Where(id => !invalidCardIds.Contains(id)).ToList() };

                QueryResponse queryResultRaw = client.MakeSyncCall<string, object, QueryResponse>(client.QueryAsyncWithJsonContext, "card-get-carddata", cardIdData);
                CardDataResponse queryResult = queryResultRaw.resultAsJson<CardDataResponse>();
                List<JObject> queryResultCardData = JsonConvert.DeserializeObject<List<JObject>>(queryResult.cardData)!;

                const int ERR_INVALID_CARDIDS_CODE = 34103;
                const string ERR_INVALID_CARDIDS_MESSAGE = "Invalid cardIDs requested";
                bool invalidCardIdsWereRequested() => queryResult.error == ERR_INVALID_CARDIDS_CODE && queryResult.message == ERR_INVALID_CARDIDS_MESSAGE;
                if (invalidCardIdsWereRequested())
                {
                    // Rainier is being cranky; break everything into individual requests
                    queryResultCardData = new List<JObject>(cardIds.Count);
                    int cardCountForSet = cardIds.Keys.Count(id => !invalidCardIds.Contains(id));
                    int fetchedCardsForSet = 0;

                    foreach (string currentCardID in cardIds.Keys.Where(id => !invalidCardIds.Contains(id)))
                    {
                        Thread.Sleep(350 /*ms*/);
                        cardIdData = new { cardIDs = (IEnumerable<string>)new string[] { currentCardID } };
                        queryResultRaw = client.MakeSyncCall<string, object, QueryResponse>(client.QueryAsyncWithJsonContext, "card-get-carddata", cardIdData);
                        queryResult = queryResultRaw.resultAsJson<CardDataResponse>();
                        if (invalidCardIdsWereRequested())
                        {
                            invalidCardIds.Add(currentCardID);
                        }
                        else if (queryResult.error != 0)
                        {
                            throw new InvalidOperationException($"Service returned unknown non-zero error {queryResult.error} - {queryResult.message}");
                        }
                        else
                        {
                            queryResultCardData.Add(JsonConvert.DeserializeObject<List<JObject>>(queryResult.cardData)![0]);
                        }
                        fetchTask?.Increment(1);
                        fetchedCardsForSet += 1;
                        if (fetchTask != null) fetchTask.Description = $"Fetch card definitions (PIECEMEAL {setName} - {fetchedCardsForSet}/{cardCountForSet})";
                    }
                }
                else if (queryResultCardData.Count == 0)
                {
                    // WTF, an empty set??? Debug it!
                }
                else
                {
                    // Everything is normal
                    fetchTask?.Increment(cardIds.Count);
                }

                foreach (JObject card in queryResultCardData)
                {
                    // Attempt to normalize the releaseDate to PT ISO dates, as they randomly warp between both UTC <--> PT and ISO <--> mm/dd/yy, and make it seem like there are more differences than there really are
                    if (card.ContainsKey("releaseDate"))
                    {
                        string? releaseDateRawValue = card.Value<string>("releaseDate");
                        if (releaseDateRawValue != null)
                        {
                            DateTime rawReleaseDate = DateTime.Parse(releaseDateRawValue);

                            // If Hour is 18 or 17, then the time is UTC. Offset is based on if PT is in DST at that time or not.
                            if (rawReleaseDate.Hour == 18) rawReleaseDate = rawReleaseDate.AddHours(-8);
                            else if (rawReleaseDate.Hour == 17) rawReleaseDate = rawReleaseDate.AddHours(-7);

                            card["releaseDate"] = new JValue(rawReleaseDate.ToString("s"));
                        }
                    }

                    string fname = card.Value<string>("cardSourceID") + ".json";
                    string fullPath = Path.Combine(setFolder, fname);

                    File.WriteAllText(fullPath, JsonConvert.SerializeObject(card));
                }

                if (invalidCardIds.Any())
                {
                    File.WriteAllLines(INVALID_CARD_IDS_FILE, invalidCardIds);
                }
            }
        }

        public static void FetchAndSaveAllGamemodeData(Client client)
        {
            foreach(GameMode mode in Enum.GetValues<GameMode>())
            {
                FetchAndSaveGamemodeData(client, mode);
            }
        }
        public static void FetchAndSaveGamemodeData(Client client, GameMode mode)
        {
            string fakeBoardEntityToReplace = Guid.NewGuid().ToString();
            BoardEntity bakedBoard = new BoardEntity(mode, false) { isPlayer1 = false, entityID = fakeBoardEntityToReplace, currentPos = BoardPos.Board, /* appliedStatusEffects = [], entityMetaData = {}, */ };
            GameContext bakedGameContext = new GameContext("game-get-gamedata") { board = bakedBoard };

            QueryResponse queryResultRaw = client.MakeSyncCall<string, object, QueryResponse>(client.QueryAsyncWithJsonContext, "game-get-gamedata", bakedGameContext);
            GameDataResponse gdrRaw = JsonConvert.DeserializeObject<GameDataResponse>(queryResultRaw.resultAsString().Replace(fakeBoardEntityToReplace, OMUKADE_FAKE_BOARD_ID));
            File.WriteAllText(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DEFINITIONS, $"game-data-{mode}.json"), gdrRaw.gameData);
        }

        public static void FetchAndSaveItemDatabase(Client client)
        {
            Directory.CreateDirectory(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DATABASE));
            ConfigDocumentGetResponse manifestDocument = client.GetConfigDocumentSync("item-set-database_0.0");

            File.WriteAllText(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DATABASE, "item-set-database.json"), manifestDocument.data["itemsets"].contentString);
        }

        internal static void FetchAndSaveCardDatabaseInteractive(Client client)
        {
            AnsiConsole.Progress()
                .HideCompleted(false)
                .AutoClear(false)
                .Start(ctx => FetchAndSaveCardDatabaseCore(client, ctx));
        }

        public static void FetchAndSaveCardDatabase(Client client) => FetchAndSaveCardDatabaseCore(client, ctx: null);

        private static void FetchAndSaveCardDatabaseCore(Client client, ProgressContext? ctx)
        {
            Directory.CreateDirectory(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DATABASE));

            ConfigCardDataTablesProvider.ConfigSettings configToUse = ConfigCardDataTablesProvider.DefaultConfigSettings;
            configToUse.batchDownloadConfigs = true;

            ConfigDocumentGetResponse manifestDocument = client.GetConfigDocumentSync(configToUse.manifestConfigName + "_" + configToUse.manifestConfigVer);
            List<string> manifestConfig = JsonConvert.DeserializeObject<List<string>>(manifestDocument.data[configToUse.manifestConfigKey].contentString)!;

            ProgressTask? fetchTask = null;
            if(ctx != null) fetchTask = ctx.AddTask("Fetch card DBs for set");

            string[] ALL_LANGUAGES = new string[] { "en" };

            int numDbsToFetch = ALL_LANGUAGES.Length * manifestConfig.Count;
            if(fetchTask != null) fetchTask.MaxValue = numDbsToFetch;

            IEnumerable<string> allDatabasesToFetch = ALL_LANGUAGES.SelectMany(lang => manifestConfig.Select(db => "card-database-" + string.Format(db, lang) + "_0.0"));
            foreach (string dbName in allDatabasesToFetch)
            {
                ConfigDocumentGetResponse dbRaw = client.GetConfigDocumentSync(dbName);
                File.WriteAllBytes(Path.Combine(Program.outputFolder, OUTPUT_FOLDER_CARD_DATABASE, dbName + ".db"), dbRaw.data["table"].contentBinary);

                string jsonFilename = Path.Combine(Program.outputFolder, OUTPUT_FOLDER_CARD_DATABASE, dbName + ".json");
                WriteDatatableToFile(dbRaw.data["table"].contentBinary, DataTableCustomFormatter.Deserialize, jsonFilename);

                fetchTask?.Increment(1.0d);
            }
        }

        public static void FetchAndSaveCardActions(Client client)
        {
            Directory.CreateDirectory(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_ACTIONS));

            ConfigDocumentGetResponse actionsDocument = client.GetConfigDocumentSync("actionsTable_0.0");
            File.WriteAllBytes(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_ACTIONS, "actions.db"), actionsDocument.data["actionsTable"].contentBinary);

            string jsonFilename = Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_ACTIONS, "actions.json");
            WriteDatatableToFile(actionsDocument.data["actionsTable"].contentBinary, DataTableCustomFormatter.Deserialize, jsonFilename);
        }

        public static void FetchAndSaveAiCustomizationData(Client client)
        {
            string aiCustomization = FetchAiCustomizationData(client);
            string aiCustomizationFolder = Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DEFINITIONS, "ai-customizations");
            Directory.CreateDirectory(aiCustomizationFolder);
            File.WriteAllText(Path.Combine(aiCustomizationFolder, $"ai-customization-{DateTime.UtcNow.Ticks}.json"), aiCustomization);
        }

        public static string FetchAiCustomizationData(Client client)
        {
            string fakeBoardEntityToReplace = Guid.NewGuid().ToString();
            OfflineMatchContext omc = new OfflineMatchContext("offline-get-ai-customizations") { gameMode = GameMode.Standard };
            QueryResponse queryResultRaw = client.MakeSyncCall<string, object, QueryResponse>(client.QueryAsyncWithJsonContext, "offline-get-ai-customizations", omc);

            OfflineMatchResponse gdrRaw = queryResultRaw.resultAsJson<OfflineMatchResponse>();
            return queryResultRaw.resultAsString();
        }

        public static void LoadActionsDb()
        {
            DataTable dt = DataTableCustomFormatter.Deserialize(File.ReadAllBytes(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_ACTIONS, "actions.db")), enableCompression: false);
            File.WriteAllText(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_ACTIONS, "actions.json"), JsonConvert.SerializeObject(dt, Formatting.Indented));
        }

        public static void LoadCardDb()
        {
            DataTable dt = DataTableCustomFormatter.Deserialize(File.ReadAllBytes(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DATABASE, "card-database-swsh11_0_en_0.0.db")), enableCompression: false);
            File.WriteAllText(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DATABASE, "card-database-swsh11_0_en_0.0.json"), JsonConvert.SerializeObject(dt, Formatting.Indented));
        }

        public static QuestQueryResponse? FetchQuestData(Client client)
        {
            Directory.CreateDirectory(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_QUEST_DATA));

            QuestContext questContext = new QuestContext("quest-setup-get-all-quests");
            QueryResponse queryResultRaw = client.MakeSyncCall<string, object, QueryResponse>(client.QueryAsyncWithJsonContext, questContext.query, questContext);
            QuestQueryResponse questData = queryResultRaw.resultAsJson<QuestQueryResponse>();

            JsonSerializerSettings jss = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                Converters = new List<JsonConverter> { new StringEnumConverter(typeof(Newtonsoft.Json.Serialization.DefaultNamingStrategy)) }
            };
            SerializeJsonToFile(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_QUEST_DATA, "current-quest-data.json"), questData, jss);

            return questData;
        }

        public static void FetchAndSaveDeckValidationRules(Client client)
        {
            string[] dbNames = new string[] { "rules-expanded_0.0", "rules-standard_0.0", "rules-dev_0.0" };

            Directory.CreateDirectory(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_DECKVALIDATION_DATA));

            foreach(string dbName in dbNames)
            {
                ConfigDocumentGetResponse dbRaw = client.GetConfigDocumentSync(dbName);
                File.WriteAllText(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_DECKVALIDATION_DATA, dbName + ".json"), dbRaw.data["rules"].contentString);
            }
        }

        public static void FetchFriends(Client client, string ptcsJwt)
        {
            List<string> profileKeys = new() { "season-rank" };
            GetAllFriendsResponse friendData = client.MakeSyncCall<string, List<string>, GetAllFriendsResponse>(client.GetAllFriendsAsync, ptcsJwt, profileKeys);
            foreach(FriendInfo friend in friendData.friendInfos)
            {
                Console.WriteLine(friend.ToString());
            }
        }

        public static void FetchFeatureFlags(Client client)
        {
            const string FEATUREFLAG_DOCUMENT_NAME = "feature-flags_0.0";

            ConfigDocumentGetResponse flagsRaw = client.GetConfigDocumentSync(FEATUREFLAG_DOCUMENT_NAME);
            File.WriteAllText(Path.Combine(Fetchers.GetOutputFolder(), OUTPUT_FOLDER_CARD_DEFINITIONS, "feature-flags.json"), flagsRaw.data["featureMap"].contentString);
        }

        private static void WriteDatatableToFile(byte[] dbDocumentValue, Func<byte[], bool, DataTable> customFormatterImplementation, string filename)
        {
            using DataTable dt = customFormatterImplementation(dbDocumentValue, false);
            using StreamWriter writer = new StreamWriter(filename, append: false, Encoding.UTF8);
            JsonSerializer serializer = new JsonSerializer()
            {
                Formatting = Formatting.Indented
            };

            serializer.Serialize(writer, dt);
        }

        private static void SerializeJsonToFile(string filename, object obj, JsonSerializerSettings? settings = null)
        {
            using StreamWriter writer = new StreamWriter(filename, append: false);
            JsonSerializer serializer = JsonSerializer.Create(settings);
            serializer.Serialize(writer, obj);
        }
    }
}
