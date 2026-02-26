using HeyRed.ImageSharp.Heif;
using HeyRed.ImageSharp.Heif.Formats.Avif;
using HeyRed.ImageSharp.Heif.Formats.Heif;
using KameraData.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PAR.ParseLib;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.Security.Authentication;

namespace PAR.PartsGrabber
{
    public class ProcessParsingResultService
    {
        private readonly IApiService _apiService;

        private readonly HttpClient _httpClient;

        private readonly ILogger _logger;

        private readonly ApiServiceOptions _apiServiceOptions;

        private readonly PlaywrightFetcher _playwrightFetcher;
        private readonly ProxiedHttpClientPool _clientPool;  // ← pool!


        public ProcessParsingResultService(
            IApiService apiService,
            IOptions<ApiServiceOptions> apiServiceOptions,
            HttpClient httpClient,
            PlaywrightFetcher playwrightFetcher,
            ProxiedHttpClientPool clientPool,
            ILogger logger)
        {
            _apiServiceOptions = apiServiceOptions.Value;
            _apiService = apiService;
            _httpClient = httpClient;
            _logger = logger;
            _playwrightFetcher = playwrightFetcher;
            _clientPool = clientPool;
        }

        public async Task UpdatePartsAndReplace(
            List<ParsingPart> parsingParts,
            PartsAndReplace part,
            List<PartSource> partSources,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); // ✅ #1 Вход

            string minName = null!;
            var replaces = new List<string>();

            var parsingPartsWithNames = parsingParts.Where(x => !string.IsNullOrEmpty(x.Name));
            var parsingPartsWithReplaces = parsingParts.Where(x => x.Replaces.Count > 0);

            if (parsingPartsWithReplaces.Count() > 0)
            {
                replaces = parsingPartsWithReplaces.SelectMany(x => x.Replaces)
                    .Distinct()
                    .Where(x => x != part.MainPartNumber)
                    .ToList();
            }

            // Статус 3: нет данных нигде
            if (parsingPartsWithNames?.Count() == 0 && replaces?.Count() == 0)
            {
                ct.ThrowIfCancellationRequested(); // ✅ #2 Перед API

                part.Status = "3";
                part.Replaces = JsonConvert.SerializeObject(new List<string> { part.MainPartNumber! });
                _logger.LogInformation($"No names/replaces. Update {part.MainPartNumber} → Status=3");

                await _apiService.Put(
                    _apiServiceOptions.BaseUrl + _apiServiceOptions.UpdatePartsAndReplacesUrl,
                    part,
                    ct); // ✅ #3 ct в API
                return;
            }

            // Статус 2: есть данные
            if (parsingPartsWithNames?.Count() > 0)
            {
                ct.ThrowIfCancellationRequested(); // ✅ #4

                var minNameLen = parsingPartsWithNames.Min(x => x.Name!.Length);
                minName = parsingPartsWithNames.First(x => x.Name!.Length == minNameLen).Name!;
                minName = minName.Length > 255 ? minName.Substring(0, 251) + "..." : minName;
            }

            // Поиск первой картинки (confidence order)
            ct.ThrowIfCancellationRequested(); // ✅ #5

            string localPictureUrl = "";
            foreach (var partsSource in partSources.OrderBy(x => x.Confidence))
            {
                var parsingPart = parsingParts.Where(x => x.PartSource != null)
                    .SingleOrDefault(x => x.PartSource.SourceName == partsSource.SourceName);
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

            _logger.LogInformation($"Update {part.MainPartNumber} → Status=2 (Name/Pic/Replaces)");

            ct.ThrowIfCancellationRequested(); // ✅ #6 Финал

            await _apiService.Put(
                _apiServiceOptions.BaseUrl + _apiServiceOptions.UpdatePartsAndReplacesUrl,
                part,
                ct); // ✅ #7 ct в API
        }


        public async Task Save(ParsingPart parsingPart, PartsAndReplace part, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
           
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
                    }, ct); // ✅ ct
            }

            if (parsingPart.Replaces.Count > 0)
            {
                foreach (var replace in parsingPart.Replaces)
                {
                    ct.ThrowIfCancellationRequested();
                    await _apiService.Post(
                        _apiServiceOptions.BaseUrl + _apiServiceOptions.AddReplacesArchiveArchiveUrl,
                        new ReplacesArchive
                        {
                            ReplaceNumber = replace,
                            PartsAndReplacesId = part.Id,
                            PartsSourcesId = parsingPart.PartSource.Id,
                            AttemptCounter = parsingPart.AttempsCount
                        }, ct); // ✅ ct
                }
            }

            if (parsingPart.ParsingPictures.Count > 0)
            {
                foreach (var parsingPicture in parsingPart.ParsingPictures)
                {
                    ct.ThrowIfCancellationRequested();

                    var pathToSaveImg = GetPath(parsingPart.PartSource.Id, part.Id);
                    string? proccessedImgPath;

                    var proxy = parsingPart.UsedProxy!;  // ← proxy из парсинга!
                    _logger.LogDebug("Save picture {PictureIndex}: proxy={Proxy} UsedPW={UsedPW} Url={Url}",
                        parsingPart.ParsingPictures.IndexOf(parsingPicture),
                        proxy.IP != "" ? $"{proxy.IP}:{proxy.Port}" : "NO_PROXY",
                        parsingPart.UsedPlaywright,
                        parsingPicture.Url);

                    if (parsingPart.UsedPlaywright)
                    {
                        proccessedImgPath = await ProcessImageViaPlaywright(
                            parsingPicture.Url, pathToSaveImg, proxy, ct);
                    }
                    else
                    {
                        proccessedImgPath = await ProcessImage(
                            parsingPicture.Url, pathToSaveImg, proxy, ct);  // ← proxy!
                    }

                    if (string.IsNullOrEmpty(proccessedImgPath))
                    {
                        _logger.LogWarning("Failed to process picture {Url}", parsingPicture.Url);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(proccessedImgPath))
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
                            }, ct); // ✅ ct
                    }
                }
            }
        }

        private async Task<string> ProcessImage(string url, string pathToSaveImg, Proxy proxy, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var uri = new Uri(url);
                var tls12Only = uri.Host.EndsWith("majorapplianceparts.ca", StringComparison.OrdinalIgnoreCase);
                var client = _clientPool.GetOrCreate(proxy, uri, tls12Only);

                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Download image from {url} failed. {response.StatusCode}");
                    return null!;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var image = Image.Load(stream);

                ct.ThrowIfCancellationRequested();

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
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Image download cancelled: {url}");
                return null!;
            }
            catch (Exception ex)
            {
                var uri = new Uri(url);
                var host = uri.Host;

                // TLS‑fallback для majorapplianceparts.ca / tls12Only‑клиента
                var isMajor = host.EndsWith("majorapplianceparts.ca", StringComparison.OrdinalIgnoreCase);
                var isTlsError =
                    ex is HttpRequestException ||
                    ex is AuthenticationException ||
                    ex.InnerException is AuthenticationException;

                if (isMajor && isTlsError)
                {
                    _logger.LogWarning(ex,
                        "TLS error on proxy client for {Url}, retrying via plain HttpClient without pool", url);

                    try
                    {
                        ct.ThrowIfCancellationRequested();

                        using var resp = await _httpClient.GetAsync(url, ct);
                        if (!resp.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("Fallback HttpClient download failed from {Url}. {StatusCode}",
                                url, resp.StatusCode);
                            return null!;
                        }

                        using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        using var image = Image.Load(stream);

                        ct.ThrowIfCancellationRequested();

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

                        _logger.LogInformation("Fallback HttpClient download success for {Url}", url);
                        return pathToSaveImg + ext;
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogWarning(fallbackEx,
                            "Fallback HttpClient also failed for {Url}", url);
                        return null!;
                    }
                }

                _logger.LogInformation($"Can't process image with url {url}");
                _logger.LogDebug($"Exception: {ex.Message}");
                return null!;
            }
        }


        private async Task<string> ProcessImageViaPlaywright(
      string url,
      string pathToSaveImg,
      Proxy proxy,
      CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogDebug("ProcessImageViaPlaywright: proxy={Proxy} → {Url}",
                    proxy.IP != "" ? $"{proxy.IP}:{proxy.Port}" : "NO_PROXY", url);

                var uri = new Uri(url);
                var bytes = await _playwrightFetcher.GetImageBytesAsync(uri, proxy, ct);

                if (bytes == null || bytes.Length == 0)
                {
                    _logger.LogInformation("Playwright: image bytes empty [{Bytes}] for {Url}, fallback ProcessImage",
                        bytes?.Length ?? 0, url);
                    return await ProcessImage(url, pathToSaveImg, proxy, ct);
                }

                _logger.LogDebug("Playwright: got {BytesLength} bytes for {Url}", bytes.Length, url);

                Image image;

                // 1. Сначала пробуем стандартные декодеры ImageSharp
                try
                {
                    using var ms = new MemoryStream(bytes);
                    image = Image.Load(ms);
                }
                catch (SixLabors.ImageSharp.UnknownImageFormatException ex)
                {
                    _logger.LogDebug(ex,
                        "ImageSharp could not detect format for {Url}. Trying HEIF/AVIF decoder.", url);

                    // 2. Fallback: пробуем HEIF/AVIF через HeifDecoder (LibHeif.Native)
                    using var ms = new MemoryStream(bytes);

                    var decoderOptions = new HeifDecoderOptions
                    {
                        DecodingMode = DecodingMode.TopLevelImages,
                        GeneralOptions = new DecoderOptions
                        {
                            MaxFrames = 1
                        }
                    };

                    var heifImage = HeifDecoder.Instance.Decode(decoderOptions, ms);

                    // Берём первый кадр как обычный ImageSharp.Image
                    image = heifImage.Frames.CloneFrame(0);
                }

                ct.ThrowIfCancellationRequested();

                // 3. Всегда сохраняем в jpeg (чтобы не размазывать форматы по диску)
                Directory.CreateDirectory(Path.GetDirectoryName(pathToSaveImg)!);

                var finalPath = pathToSaveImg + ".jpeg";
                using (var fs = new FileStream(finalPath, FileMode.Create))
                {
                    await image.SaveAsJpegAsync(fs, new JpegEncoder { Quality = 90 }, ct);
                }

                image.Dispose();

                _logger.LogDebug("Playwright: saved {Path} (jpeg) from {Url}", finalPath, url);
                return finalPath;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("ProcessImageViaPlaywright cancelled: {Url}", url);
                return null!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProcessImageViaPlaywright failed: {Url}", url);
                return await ProcessImage(url, pathToSaveImg, proxy, ct);
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

    }
}