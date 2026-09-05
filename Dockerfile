# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY WordpressMCPSharp.csproj ./
ARG TARGETARCH
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet restore WordpressMCPSharp.csproj \
    -r "$rid" \
    -p:PublishSingleFile=true \
    -p:SelfContained=false \
    -p:EnableCompressionInSingleFile=false

COPY . .
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet publish WordpressMCPSharp.csproj \
    -c Release \
    --no-restore \
    -r "$rid" \
    --self-contained false \
    -o /app/publish \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    WORDPRESSMCP_Server__Host=0.0.0.0 \
    WORDPRESSMCP_Server__Port=5720 \
    WORDPRESSMCP_Server__Path=/mcp \
    WORDPRESSMCP_Server__Password= \
    WORDPRESSMCP_Wordpress__ReadOnly=true \
    WORDPRESSMCP_Wordpress__AllowDelete=false \
    WORDPRESSMCP_Wordpress__AllowPluginInstall=false \
    WORDPRESSMCP_Wordpress__AllowRestPassthrough=false \
    WORDPRESSMCP_Management__AllowCliManagement=false \
    WORDPRESSMCP_Management__AllowArbitraryCli=false \
    WORDPRESSMCP_Management__AllowProvisioning=false \
    WORDPRESSMCP_Snapshots__AllowRestore=false

# Configure sites at run time, for example:
#   -e WORDPRESSMCP_Endpoints__site1__RestApi__BaseUrl=https://example.com/ \
#   -e WORDPRESSMCP_Endpoints__site1__RestApi__Username=admin \
#   -e "WORDPRESSMCP_Endpoints__site1__RestApi__ApplicationPassword=xxxx xxxx xxxx xxxx" \
#   -e WORDPRESSMCP_DefaultSite=site1

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5720
VOLUME ["/app/logs"]

ENTRYPOINT ["./WordpressMCPSharp"]
