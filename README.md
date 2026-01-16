# Paparr - Self-Hosted Ebook Ingestion Service

Paparr is a production-ready, dockerized ebook ingestion service inspired by Radarr. It automates the process of organizing ebooks from various sources, extracting metadata, and maintaining a well-organized digital library.

## Features

- ` **Automated File Ingestion**: Polls a watch directory and processes new EPUB and PDF files
- ` **Smart Metadata Extraction**: Extracts from embedded metadata, filename parsing, Open Library and Google Books APIs
- ` **Manual Review Interface**: Clean React UI for approving or selecting metadata candidates
- ` **Calibre-Compatible Structure**: Organizes books in / library/Author/Title/ structure
- ` **PostgreSQL Backend**: Persistent storage with Entity Framework Core
- ` **Docker Support**: Complete docker-compose setup for one-command deployment
- ` **Swagger UI**: Built-in API documentation (development mode)

## Tech Stack

### Backend
- **Framework**: ASP.NET Core 8 Web API
- **Database**: PostgreSQL 16
- **ORM**: Entity Framework Core 8
- **Logging**: Serilog
- **API Docs**: Swagger/OpenAPI

### Frontend
- **Framework**: React 18
- **Build Tool**: Vite
- **HTTP Client**: Axios

## Quick Start with Docker

`ash
cd Paparr
docker-compose up --build
`

Access:
- **UI**: http://localhost:5173
- **API**: http://localhost:5000/api
- **Swagger**: http://localhost:5000/swagger

## Local Development

### Backend Setup
`ash
cd src/Paparr.API
dotnet restore
dotnet ef database update
dotnet run
`

### Frontend Setup
`ash
cd src/Paparr.UI
npm install
npm run dev
`

## API Endpoints

- GET /api/imports - List all import jobs
- GET /api/imports/{id} - Get specific import job
- POST /api/imports/{id}/accept/{candidateId} - Accept a metadata candidate
- POST /api/imports/{id}/retry - Retry a failed import

## Project Structure

`
Paparr/
 src/
    Paparr.API/              # ASP.NET Core backend
       Domain/              # Entity models
       Data/                # Database context & migrations
       Services/            # Business logic
       Controllers/         # API endpoints
       Models/              # DTOs
    Paparr.UI/               # React frontend
        src/
            components/      # React components
            pages/           # Page components
 docker/
    Dockerfile.api
    Dockerfile.ui
 docker-compose.yml
`

## Environment Variables

`json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Port=5432;Database=paparr;Username=postgres;Password=postgres"
  },
  "IngestPath": "/ingest",
  "LibraryPath": "/library",
  "PollingIntervalSeconds": "30",
  "AllowedOrigins": "http://localhost:5173;http://localhost:3000"
}
`

## Metadata Extraction

### Priority Order
1. **Embedded Metadata** (EPUB/PDF metadata)
2. **Filename Parsing** (format: "Title - Author.ext")
3. **Open Library API** (https://openlibrary.org)
4. **Google Books API** (https://www.googleapis.com/books/v1/volumes)

### Confidence Scoring
- Embedded/parsed metadata: 85%
- API results: Calculated via Levenshtein distance (0-100%)
- Auto-accept threshold: 90%

## File Organization

Books are organized in Calibre-compatible structure:

`
/library/
 Author Name/
    Book Title/
        Book Title.epub
`

## License

MIT
