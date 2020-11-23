using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using PhoenixAdult.Configuration;
using PhoenixAdult.Helpers;
using PhoenixAdult.Helpers.Utils;

namespace PhoenixAdult.Sites
{
    public class NetworkJAVLibrary : IProviderBase
    {
        public async Task<List<RemoteSearchResult>> Search(int[] siteNum, string searchTitle, DateTime? searchDate, CancellationToken cancellationToken)
        {
            var result = new List<RemoteSearchResult>();
            if (siteNum == null || string.IsNullOrEmpty(searchTitle))
            {
                return result;
            }

            string searchJAVID = null;
            var splitedTitle = searchTitle.Split();
            if (splitedTitle.Length > 1 && int.TryParse(splitedTitle[1], out _))
            {
                searchJAVID = $"{splitedTitle[0]}-{splitedTitle[1]}";
            }

            if (!string.IsNullOrEmpty(searchJAVID))
            {
                searchTitle = searchJAVID;
            }

            var url = Helper.GetSearchSearchURL(siteNum) + searchTitle;
            var http = await HTTP.Request(url, cancellationToken, false).ConfigureAwait(false);
            var data = HTML.ElementFromStream(http.ContentStream);

            var searchResults = data.SelectNodes("//div[@class='videos']//div[@class='video']");
            if (searchResults != null)
            {
                foreach (var searchResult in searchResults)
                {
                    string sceneURL = $"{Helper.GetSearchBaseURL(siteNum)}/en/?v={searchResult.SelectSingleText(".//a/@id")}",
                        curID = $"{siteNum[0]}#{siteNum[1]}#{Helper.Encode(sceneURL)}",
                        sceneName = searchResult.SelectSingleText(".//div[@class='title']"),
                        scenePoster = $"http:{searchResult.SelectSingleText(".//img/@src").Replace("ps.", "pl.", StringComparison.OrdinalIgnoreCase)}",
                        javID = searchResult.SelectSingleText(".//div[@class='id']");

                    var res = new RemoteSearchResult
                    {
                        ProviderIds = { { Plugin.Instance.Name, curID } },
                        Name = $"{javID} {sceneName}",
                        ImageUrl = scenePoster,
                    };

                    if (!string.IsNullOrEmpty(searchJAVID))
                    {
                        res.IndexNumber = 100 - LevenshteinDistance.Calculate(searchJAVID, javID);
                    }

                    result.Add(res);
                }
            }
            else
            {
                string sceneURL = $"{Helper.GetSearchBaseURL(siteNum)}/en/?v={http.Headers.Location.ToString().Split('=')[1]}",
                    curID = $"{siteNum[0]}#{siteNum[1]}#{Helper.Encode(sceneURL)}";
                string[] sceneID = curID.Split('#').Skip(2).ToArray();

                var sceneData = await this.Update(siteNum, sceneID, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(sceneData.Item.Name))
                {
                    sceneData.Item.ProviderIds.Add(Plugin.Instance.Name, curID);
                    var posters = (await this.GetImages(siteNum, sceneID, sceneData.Item, cancellationToken).ConfigureAwait(false)).Where(item => item.Type == ImageType.Primary);

                    var res = new RemoteSearchResult
                    {
                        ProviderIds = sceneData.Item.ProviderIds,
                        Name = $"{sceneData.Item.OriginalTitle} {sceneData.Item.Name}",
                        PremiereDate = sceneData.Item.PremiereDate,
                    };

                    if (posters.Any())
                    {
                        res.ImageUrl = posters.First().Url;
                    }

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

            var sceneURL = Helper.Decode(sceneID[0]);
            var sceneData = await HTML.ElementFromURL(sceneURL, cancellationToken).ConfigureAwait(false);

            var javID = sceneData.SelectSingleText("//div[@id='video_id']//td[@class='text']");

            result.Item.OriginalTitle = javID.ToUpperInvariant();
            result.Item.Name = sceneData.SelectSingleText("//div[@id='video_title']//h3").Replace(javID, string.Empty, StringComparison.OrdinalIgnoreCase);

            result.Item.AddStudio(sceneData.SelectSingleText("//div[@id='video_maker']//td[@class='text']"));

            var date = sceneData.SelectSingleText("//div[@id='video_date']//td[@class='text']");
            if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime sceneDateObj))
            {
                result.Item.PremiereDate = sceneDateObj;
            }

            var genreNode = sceneData.SelectNodes("//div[@id='video_genres']//td[@class='text']//a");
            if (genreNode != null)
            {
                foreach (var genreLink in genreNode)
                {
                    var genreName = genreLink.InnerText;

                    result.Item.AddGenre(genreName);
                }
            }

            var actorsNode = sceneData.SelectNodes("//div[@id='video_cast']//td[@class='text']//span[@class='cast']//a");
            if (actorsNode != null)
            {
                foreach (var actorLink in actorsNode)
                {
                    string actorName = actorLink.InnerText;

                    if (actorName != "----")
                    {
                        if (Plugin.Instance.Configuration.JAVActorNamingStyle == JAVActorNamingStyle.WesternStyle)
                        {
                            actorName = string.Join(" ", actorName.Split().Reverse());
                        }

                        var actor = new PersonInfo
                        {
                            Name = actorName,
                        };

                        result.People.Add(actor);
                    }
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

            var sceneURL = Helper.Decode(sceneID[0]);
            var sceneData = await HTML.ElementFromURL(sceneURL, cancellationToken).ConfigureAwait(false);

            var img = sceneData.SelectSingleText("//img[@id='video_jacket_img']/@src");
            if (!string.IsNullOrEmpty(img))
            {
                result.Add(new RemoteImageInfo
                {
                    Url = $"http:{img}",
                    Type = ImageType.Primary,
                });
            }

            var sceneImages = sceneData.SelectNodes("//div[@class='previewthumbs']/img");
            if (sceneImages != null)
            {
                foreach (var sceneImage in sceneImages)
                {
                    img = $"http:{sceneImage.Attributes["src"].Value.Replace("-", "jp-", StringComparison.OrdinalIgnoreCase)}";

                    result.Add(new RemoteImageInfo
                    {
                        Url = img,
                        Type = ImageType.Primary,
                    });

                    result.Add(new RemoteImageInfo
                    {
                        Url = img,
                        Type = ImageType.Backdrop,
                    });
                }
            }

            return result;
        }
    }
}