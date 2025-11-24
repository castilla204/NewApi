FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
EXPOSE 7124

FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /src
COPY ["newApi.csproj", "./"]
RUN dotnet restore "newApi.csproj"
COPY . .
RUN dotnet build "newApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "newApi.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "newApi.dll"]