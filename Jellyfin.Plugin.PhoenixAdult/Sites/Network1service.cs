using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PhoenixAdult.Helpers;
using PhoenixAdult.Helpers.Utils;

namespace PhoenixAdult.Sites
{
    public class Network1service : IProviderBase
    {
        public static async Task<string> GetToken(int[] siteNum, CancellationToken cancellationToken)
        {
            string result = string.Empty;

            if (siteNum == null)
            {
                return result;
            }

            var db = new JObject();
            if (!string.IsNullOrEmpty(Plugin.Instance.Configuration.TokenStorage))
            {
                db = JObject.Parse(Plugin.Instance.Configuration.TokenStorage);
            }

            var keyName = new Uri(Helper.GetSearchBaseURL(siteNum)).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (db.ContainsKey(keyName))
            {
                string token = (string)db[keyName],
                    source = ((string)db[keyName]).Split('.')[1] + "=",
                    res = Encoding.UTF8.GetString(Convert.FromBase64String(source));

                if ((int)JObject.Parse(res)["exp"] > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    result = token;
                }
            }
            else
            {
                var cookies = await GetCookies(Helper.GetSearchBaseURL(siteNum), cancellationToken).ConfigureAwait(false);
                var instanceToken = cookies.Where(o => o.Name == "instance_token");
                if (!instanceToken.Any())
                {
                    return result;
                }

                result = instanceToken.First().Value;

                if (db.ContainsKey(keyName))
                {
                    db[keyName] = result;
                }
                else
                {
                    db.Add(keyName, result);
                }

                Plugin.Instance.Configuration.TokenStorage = JsonConvert.SerializeObject(db);
                Plugin.Instance.SaveConfiguration();
            }

            return result;
        }

        public static async Task<IList<Cookie>> GetCookies(string url, CancellationToken cancellationToken)
        {
            var http = await HTTP.Request(url, HttpMethod.Head, cancellationToken).ConfigureAwait(false);

            return http.Cookies;
        }

        public static async Task<JObject> GetDataFromAPI(string url, string instance, CancellationToken cancellationToken)
        {
            JObject json = null;
            var headers = new Dictionary<string, string>
            {
                { "Instance", instance },
            };

            var http = await HTTP.Request(url, HTTP.CreateRequest(headers), cancellationToken).ConfigureAwait(false);
            if (http.IsOK)
            {
                json = JObject.Parse(http.Content);
            }

            return json;
        }

        public async Task<List<RemoteSearchResult>> Search(int[] siteNum, string searchTitle, DateTime? searchDate, CancellationToken cancellationToken)
        {
            var result = new List<RemoteSearchResult>();
            if (siteNum == null || string.IsNullOrEmpty(searchTitle))
            {
                return result;
            }

            var searchSceneID = searchTitle.Split()[0];
            var sceneTypes = new List<string> { "scene", "movie", "serie" };
            if (!int.TryParse(searchSceneID, out _))
            {
                searchSceneID = null;
            }

            var instanceToken = await GetToken(siteNum, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(instanceToken))
            {
                return result;
            }

            foreach (var sceneType in sceneTypes)
            {
                string url;
                if (string.IsNullOrEmpty(searchSceneID))
                {
                    url = $"/v2/releases?type={sceneType}&search={searchTitle}";
                }
                else
                {
                    url = $"/v2/releases?type={sceneType}&id={searchSceneID}";
                }

                var searchResults = await GetDataFromAPI(Helper.GetSearchSearchURL(siteNum) + url, instanceToken, cancellationToken).ConfigureAwait(false);
                if (searchResults == null)
                {
                    break;
                }

                foreach (var searchResult in searchResults["result"])
                {
                    string sceneID = (string)searchResult["id"],
                            curID = $"{siteNum[0]}#{siteNum[1]}#{sceneID}#{sceneType}",
                            sceneName = (string)searchResult["title"],
                            scenePoster = string.Empty;
                    DateTime sceneDateObj = (DateTime)searchResult["dateReleased"];

                    var imageTypes = new List<string> { "poster", "cover" };
                    foreach (var imageType in imageTypes)
                    {
                        if (searchResult["images"][imageType] != null)
                        {
                            foreach (var image in searchResult["images"][imageType])
                            {
                                scenePoster = (string)image["xx"]["url"];
                            }
                        }
                    }

                    var res = new RemoteSearchResult
                    {
                        ProviderIds = { { Plugin.Instance.Name, curID } },
                        Name = sceneName,
                        ImageUrl = scenePoster,
                        PremiereDate = sceneDateObj,
                    };

                    result.Add(res);
                }
            }

            return result;
        }

        public async Task<MetadataResult<Movie>> Update(int[] siteNum, string[] sceneID, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>()
            {
                Item = new Movie(),
                People = new List<PersonInfo>(),
            };

            if (sceneID == null)
            {
                return result;
            }

            var instanceToken = await GetToken(siteNum, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(instanceToken))
            {
                return result;
            }

            var url = $"{Helper.GetSearchSearchURL(siteNum)}/v2/releases?type={sceneID[1]}&id={sceneID[0]}";
            var sceneData = await GetDataFromAPI(url, instanceToken, cancellationToken).ConfigureAwait(false);
            if (sceneData == null)
            {
                return result;
            }

            sceneData = (JObject)sceneData["result"].First;

            result.Item.Name = (string)sceneData["title"];
            result.Item.Overview = (string)sceneData["description"];
            result.Item.AddStudio(CultureInfo.InvariantCulture.TextInfo.ToTitleCase((string)sceneData["brand"]));

            DateTime sceneDateObj = (DateTime)sceneData["dateReleased"];
            result.Item.PremiereDate = sceneDateObj;

            foreach (var genreLink in sceneData["tags"])
            {
                var genreName = (string)genreLink["name"];

                result.Item.AddGenre(genreName);
            }

            foreach (var actorLink in sceneData["actors"])
            {
                var actorPageURL = $"{Helper.GetSearchSearchURL(siteNum)}/v1/actors?id={actorLink["id"]}";
                var actorData = await GetDataFromAPI(actorPageURL, instanceToken, cancellationToken).ConfigureAwait(false);
                if (actorData != null)
                {
                    actorData = (JObject)actorData["result"].First;

                    var actor = new PersonInfo
                    {
                        Name = (string)actorLink["name"],
                    };

                    if (actorData["images"] != null && actorData["images"].Type == JTokenType.Object)
                    {
                        actor.ImageUrl = (string)actorData["images"]["profile"].First["xs"]["url"];
                    }

                    result.People.Add(actor);
                }
            }

            return result;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(int[] siteNum, string[] sceneID, BaseItem item, CancellationToken cancellationToken)
        {
            var result = new List<RemoteImageInfo>();

            if (sceneID == null)
            {
                return result;
            }

            var instanceToken = await GetToken(siteNum, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(instanceToken))
            {
                return result;
            }

            var url = $"{Helper.GetSearchSearchURL(siteNum)}/v2/releases?type={sceneID[1]}&id={sceneID[0]}";
            var sceneData = await GetDataFromAPI(url, instanceToken, cancellationToken).ConfigureAwait(false);
            if (sceneData == null)
            {
                return result;
            }

            sceneData = (JObject)sceneData["result"].First;

            var imageTypes = new List<string> { "poster", "cover" };
            foreach (var imageType in imageTypes)
            {
                if (sceneData["images"][imageType] != null)
                {
                    foreach (var image in sceneData["images"][imageType])
                    {
                        result.Add(new RemoteImageInfo
                        {
                            Url = (string)image["xx"]["url"],
                            Type = ImageType.Primary,
                        });
                        result.Add(new RemoteImageInfo
                        {
                            Url = (string)image["xx"]["url"],
                            Type = ImageType.Backdrop,
                        });
                    }
                }
            }

            return result;
        }
    }
}
