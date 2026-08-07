# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Build context must be D:\job\pasha\Git because this project references
# sibling projects: 2_KameraData and PAR.ParseLib-main.
COPY PAR.PartsGrabber_2026/PAR.PartsGrabber.csproj PAR.PartsGrabber_2026/
COPY 2_KameraData/KameraData.csproj 2_KameraData/
COPY PAR.ParseLib-main/PAR.ParseLib.csproj PAR.ParseLib-main/

COPY PAR.PartsGrabber_2026 PAR.PartsGrabber_2026
COPY 2_KameraData 2_KameraData
COPY PAR.ParseLib-main PAR.ParseLib-main
RUN rm -rf PAR.PartsGrabber_2026/bin \
           PAR.PartsGrabber_2026/obj \
           2_KameraData/bin \
           2_KameraData/obj \
           PAR.ParseLib-main/bin \
           PAR.ParseLib-main/obj \
 && for i in 1 2 3 4 5; do \
      dotnet restore PAR.PartsGrabber_2026/PAR.PartsGrabber.csproj \
        --source https://www.nuget.org/api/v2/ \
        --disable-parallel \
      && break; \
      if [ "$i" = "5" ]; then exit 1; fi; \
      echo "dotnet restore failed, retry $i/5"; \
      sleep 15; \
    done

RUN dotnet publish PAR.PartsGrabber_2026/PAR.PartsGrabber.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false \
 && rm -f /app/publish/appsettings.json

FROM mcr.microsoft.com/playwright/dotnet:v1.58.0-noble AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0
EXPOSE 9105

COPY --from=build /app/publish ./

# Mount environment-specific appsettings.json to /app/appsettings.json.
ENTRYPOINT ["dotnet", "PAR.PartsGrabber.dll"]
