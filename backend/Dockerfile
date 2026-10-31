FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Chat.slnx ./
COPY src/Chat.Data/Chat.Data.csproj src/Chat.Data/
COPY src/Chat.Api/Chat.Api.csproj src/Chat.Api/
COPY tests/Chat.Data.Tests/Chat.Data.Tests.csproj tests/Chat.Data.Tests/
RUN dotnet restore
COPY . .
RUN dotnet publish src/Chat.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Chat.Api.dll"]
