# The sample (EKG.Common.LeaderElection.Sample), deployed by the Tiltfile to kind for the E2E tests.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY EKG.Common.LeaderElection/*.csproj          EKG.Common.LeaderElection/
COPY EKG.Common.LeaderElection.Sample/*.csproj   EKG.Common.LeaderElection.Sample/

RUN dotnet restore "EKG.Common.LeaderElection.Sample/EKG.Common.LeaderElection.Sample.csproj"

FROM restore AS publish
COPY EKG.Common.LeaderElection/          EKG.Common.LeaderElection/
COPY EKG.Common.LeaderElection.Sample/   EKG.Common.LeaderElection.Sample/
RUN dotnet publish "EKG.Common.LeaderElection.Sample/EKG.Common.LeaderElection.Sample.csproj" \
    -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "EKG.Common.LeaderElection.Sample.dll"]
