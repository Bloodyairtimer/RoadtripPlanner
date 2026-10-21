FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/RoadtripPlanner/RoadtripPlanner.csproj src/RoadtripPlanner/
RUN dotnet restore src/RoadtripPlanner/RoadtripPlanner.csproj
COPY src/RoadtripPlanner/ src/RoadtripPlanner/
RUN dotnet publish src/RoadtripPlanner/RoadtripPlanner.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_HTTP_PORTS=8080 Data__Directory=/data
USER app
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "RoadtripPlanner.dll"]
