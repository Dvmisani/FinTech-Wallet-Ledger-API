FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/FinTechWallet.Api/FinTechWallet.Api.csproj", "src/FinTechWallet.Api/"]
RUN dotnet restore "src/FinTechWallet.Api/FinTechWallet.Api.csproj"
COPY . .
WORKDIR "/src/src/FinTechWallet.Api"
RUN dotnet publish "FinTechWallet.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FinTechWallet.Api.dll"]
