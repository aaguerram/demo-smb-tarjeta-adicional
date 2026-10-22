# Compilación con el SDK .NET 10 de Red Hat (UBI 9).
FROM registry.access.redhat.com/ubi9/dotnet-100:latest AS build
WORKDIR /opt/app-root/src
COPY --chown=1001:0 *.csproj ./
RUN dotnet restore
COPY --chown=1001:0 *.cs ./
RUN dotnet publish -c Release -o /opt/app-root/publish --no-restore

# Ejecución con el runtime ASP.NET Core 10 de Red Hat (UBI 9), igual que en OpenShift.
FROM registry.access.redhat.com/ubi9/dotnet-100-aspnet:latest
WORKDIR /opt/app-root/app
COPY --from=build /opt/app-root/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1001
ENTRYPOINT ["dotnet", "SdGenerarSolicitudTarjetaPdf.dll"]
