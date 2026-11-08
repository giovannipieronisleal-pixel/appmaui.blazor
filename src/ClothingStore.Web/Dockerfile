# =============================================================================
# Dockerfile para hospedagem 24/7 do Site Catálogo (ClothingStore.Web) no Google Cloud Run
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ClothingStore.Domain/ClothingStore.Domain.csproj", "src/ClothingStore.Domain/"]
COPY ["src/ClothingStore.Shared/ClothingStore.Shared.csproj", "src/ClothingStore.Shared/"]
COPY ["src/ClothingStore.Application/ClothingStore.Application.csproj", "src/ClothingStore.Application/"]
COPY ["src/ClothingStore.Infrastructure/ClothingStore.Infrastructure.csproj", "src/ClothingStore.Infrastructure/"]
COPY ["src/ClothingStore.Web/ClothingStore.Web.csproj", "src/ClothingStore.Web/"]

RUN dotnet restore "src/ClothingStore.Web/ClothingStore.Web.csproj"

COPY src/ src/
WORKDIR "/src/src/ClothingStore.Web"
RUN dotnet publish "ClothingStore.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ClothingStore.Web.dll"]

