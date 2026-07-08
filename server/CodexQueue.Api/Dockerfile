FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY CodexQueue.slnx ./
COPY server/CodexQueue.Api/CodexQueue.Api.csproj server/CodexQueue.Api/
RUN dotnet restore server/CodexQueue.Api/CodexQueue.Api.csproj

COPY server/CodexQueue.Api/ server/CodexQueue.Api/
RUN dotnet publish server/CodexQueue.Api/CodexQueue.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git openssh-client \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/data /home/app/.ssh \
    && chmod 700 /home/app/.ssh

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV HOME=/home/app
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CodexQueue.Api.dll"]
