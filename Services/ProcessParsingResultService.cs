using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PAR.ParseLib;
using SixLabors.ImageSharp;

namespace PAR.PartsGrabber
{
    public class ProcessParsingResultService
    {
        private readonly IApiService _apiService;

        private readonly HttpClient _httpClient;

        private readonly ILogger _logger;

        private readonly ApiServiceOptions _apiServiceOptions;

        public ProcessParsingResultService(
            IApiService apiService,
            IOptions<ApiServiceOptions> apiServiceOptions,
            HttpClient httpClient,
            ILogger logger)
        {
            _apiServiceOptions = apiServiceOptions.Value;
            _apiService = apiService;
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task UpdatePartsAndReplace(List<ParsingPart> parsingParts, PartsAndReplace part, List<PartSource> partSources)
        {
            string minName = null!;
            var replaces = new List<string>();

            var parsingPartsWithNames = parsingParts.Where(x => !string.IsNullOrEmpty(x.Name));
            var parsingPartsWithReplaces = parsingParts.Where(x => x.Replaces.Count > 0);
            if (parsingPartsWithReplaces.Count() > 0)
            {
                replaces = parsingPartsWithReplaces.SelectMany(x => x.Replaces).Distinct().Where(x => x != part.MainPartNumber).ToList();
            }

            if (parsingPartsWithNames?.Count() == 0 && replaces?.Count() == 0)
            {
                part.Status = "3";
                part.Replaces = JsonConvert.SerializeObject(new List<string> { part.MainPartNumber! });
                _logger.LogInformation($"There are no names and replaces. Update parts and replaces {part.MainPartNumber} with status 3");

                await _apiService.Put(_apiServiceOptions.BaseUrl + _apiServiceOptions.UpdatePartsAndReplacesUrl, part);
                return;
            }

            if (parsingPartsWithNames?.Count() > 0)
            {
                var minNameLen = parsingPartsWithNames.Min(x => x.Name!.Length);
                minName = parsingPartsWithNames.First(x => x.Name!.Length == minNameLen).Name!;
                minName = minName.Length > 255 ? minName.Substring(0, 251) + "..." : minName;
            }

            string localPictureUrl = "";
            foreach (var partsSource in partSources.OrderBy(x => x.Confidence))
            {
                var parsingPart = parsingParts.Where(x => x.PartSource != null).SingleOrDefault(x => x.PartSource.SourceName == partsSource.SourceName);
                if (parsingPart?.ParsingPictures.Count > 0)
                {
                    localPictureUrl = parsingPart.ParsingPictures.First().LocalPath;
                    break;
                }
            }

            var mainPartNumberReplaces = new List<string> { part.MainPartNumber! };
            mainPartNumberReplaces.AddRange(replaces!);

            part.PartName = !string.IsNullOrEmpty(minName) ? minName : null;
            part.Replaces = JsonConvert.SerializeObject(mainPartNumberReplaces);
            part.Pic = localPictureUrl;
            part.PhotoStatus = string.IsNullOrEmpty(localPictureUrl) ? null : 1;
            part.Status = "2";
            _logger.LogInformation($"Update parts and replaces {part.MainPartNumber} with status 2");

            await _apiService.Put(_apiServiceOptions.BaseUrl + _apiServiceOptions.UpdatePartsAndReplacesUrl, part);
        }

        public async Task Save(ParsingPart parsingPart, PartsAndReplace part)
        {
            if (!string.IsNullOrEmpty(parsingPart.Name))
            {
                var parsingPartName = parsingPart.Name.Length > 255 ? parsingPart.Name.Substring(0, 251) + "..." : parsingPart.Name;
                await _apiService.Post(
                    _apiServiceOptions.BaseUrl + _apiServiceOptions.AddPartsNamesArchiveUrl,
                    new PartsNamesArchive
                    {
                        PartName = parsingPartName,
                        PartsAndReplacesId = part.Id,
                        PartsSourcesId = parsingPart.PartSource.Id,
                        AttemptCounter = parsingPart.AttempsCount
                    });
            }

            if (parsingPart.Replaces.Count > 0)
            {
                foreach (var replace in parsingPart.Replaces)
                {
                    await _apiService.Post(
                        _apiServiceOptions.BaseUrl + _apiServiceOptions.AddReplacesArchiveArchiveUrl,
                        new ReplacesArchive
                        {
                            ReplaceNumber = replace,
                            PartsAndReplacesId = part.Id,
                            PartsSourcesId = parsingPart.PartSource.Id,
                            AttemptCounter = parsingPart.AttempsCount
                        });
                }
            }

            if (parsingPart.ParsingPictures.Count > 0)
            {
                foreach (var parsingPicture in parsingPart.ParsingPictures)
                {
                    var pathToSaveImg = GetPath(parsingPart.PartSource.Id, part.Id);
                    var proccessedImgPath = await ProcessImage(parsingPicture.Url, pathToSaveImg);
                    var pathLimits = proccessedImgPath.Length <= 255 && parsingPicture.Url.Length <= 255;
                    if (!pathLimits)
                    {
                        _logger.LogError($"Can't save picture through the API. parsing part {parsingPart.Name}");
                        _logger.LogError($"Local path or link has a size of more than 255 characters");
                    }

                    if (pathLimits && !string.IsNullOrEmpty(proccessedImgPath))
                    {
                        parsingPicture.LocalPath = proccessedImgPath;
                        await _apiService.Post(
                            _apiServiceOptions.BaseUrl + _apiServiceOptions.AddPartsPicArchiveUrl,
                            new PartsPicArchive
                            {
                                PartsAndReplacesId = part.Id,
                                Link = parsingPicture.Url,
                                LocalPath = proccessedImgPath,
                                PartsSourcesId = parsingPart.PartSource.Id,
                                AttemptCounter = parsingPart.AttempsCount
                            });
                    }
                }
            }
        }

        private string GetPath(int partsSourcesId, int partsReplacesId)
        {
            var nextFileNameInt = 1;
            var dir = Directory.CreateDirectory(@$"parts/pic/{partsSourcesId}/{partsReplacesId}/");
            var files = dir.GetFiles().Where(x => x.Name.Replace(x.Extension, "").All(y => char.IsDigit(y)));
            if (files.Count() > 0)
            {
                nextFileNameInt = files.Select(x => Convert.ToInt32(x.Name.Replace(x.Extension, ""))).Max() + 1;
            }

            return dir.FullName + nextFileNameInt.ToString();
        }

        private async Task<string> ProcessImage(string url, string pathToSaveImg)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Download image from {url} failed. {response.StatusCode}");
                    return null!;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var image = Image.Load(stream);
                var format = image.Metadata.DecodedImageFormat!;
                var ext = format.Name.ToLower() == "webp" ? ".jpg" : "." + format.Name.ToLower();
                using var fs = new FileStream(pathToSaveImg + ext, FileMode.OpenOrCreate);
                if (format.Name.ToLower() == "webp")
                {
                    image.SaveAsJpeg(fs);
                }
                else
                {
                    image.Save(fs, format);
                }

                return pathToSaveImg + ext;
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Can't process image with url {url}");
                _logger.LogDebug($"Exception: {ex.Message}");
                return null!;
            }
        }
    }
}