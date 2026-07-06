# syntax=docker/dockerfile:1
# r8er control plane (API + WebRTC signaling) image for Railway.
# Set the Railway service's Root Directory to "backend" so this Dockerfile is found.
# Base tags float at patch level on purpose — they pick up security updates.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the API project (the test project isn't in the image).
# Separate COPY so this layer caches until the csproj itself changes.
COPY src/R8er.Api/R8er.Api.csproj src/R8er.Api/
RUN dotnet restore src/R8er.Api/R8er.Api.csproj

COPY src/ src/
RUN dotnet publish src/R8er.Api/R8er.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# Railway injects $PORT; Program.cs binds 0.0.0.0:$PORT when it is present.
ENTRYPOINT ["dotnet", "R8er.Api.dll"]
