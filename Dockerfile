FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DialogueSystem.Domain/DialogueSystem.Domain.csproj src/DialogueSystem.Domain/
COPY src/DialogueSystem.Application/DialogueSystem.Application.csproj src/DialogueSystem.Application/
COPY src/DialogueSystem.Infrastructure/DialogueSystem.Infrastructure.csproj src/DialogueSystem.Infrastructure/
COPY src/DialogueSystem.Web/DialogueSystem.Web.csproj src/DialogueSystem.Web/
RUN dotnet restore src/DialogueSystem.Web/DialogueSystem.Web.csproj
COPY examples/ examples/
COPY src/ src/
RUN dotnet publish src/DialogueSystem.Web/DialogueSystem.Web.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown app:app /data
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DialogueSystem__DataDirectory=/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "DialogueSystem.Web.dll"]
