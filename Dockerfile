FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/BillingOperations/BillingOperations.csproj src/BillingOperations/
RUN dotnet restore src/BillingOperations/BillingOperations.csproj
COPY src/BillingOperations/ src/BillingOperations/
RUN dotnet publish src/BillingOperations/BillingOperations.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BillingOperations.dll"]
