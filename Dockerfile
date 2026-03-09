FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /App

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore LocalList.API.NET.csproj
# Build and publish a release
RUN dotnet publish LocalList.API.NET.csproj -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /App
COPY --from=build-env /App/out .
USER app
ENTRYPOINT ["dotnet", "LocalList.API.NET.dll"]
