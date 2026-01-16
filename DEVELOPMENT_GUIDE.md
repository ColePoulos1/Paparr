# Paparr Development Guide

## Local Development Setup

### Prerequisites
- .NET 8 SDK
- Node.js 20+
- PostgreSQL 16
- Docker (optional)

### Database Setup

1. **Install PostgreSQL** and create a database:
```bash
createdb paparr
```

2. **Update connection string** in `src/Paparr.API/appsettings.Development.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=paparr;Username=postgres;Password=YOUR_PASSWORD"
  }
}
```

3. **Run migrations**:
```bash
cd src/Paparr.API
dotnet ef database update
```

### Backend Development

```bash
cd src/Paparr.API
dotnet restore
dotnet run
```

The API will start on `https://localhost:7091` (development) or `http://localhost:5000`.

**Features in development:**
- Hot reload enabled
- Swagger UI at `https://localhost:7091/swagger`
- Detailed error messages
- Debug logging enabled

### Frontend Development

```bash
cd src/Paparr.UI
npm install
npm run dev
```

The UI will start on `http://localhost:5173` with Vite's hot reload.

**Features in development:**
- Hot module replacement (HMR)
- Proxy to API at http://localhost:5000
- Fast build times
- React DevTools compatible

### Adding Features

#### Adding a New Entity

1. Create class in `Domain/`:
```csharp
namespace Paparr.API.Domain;

public class MyEntity
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

2. Add DbSet to `Data/AppDbContext.cs`:
```csharp
public DbSet<MyEntity> MyEntities { get; set; } = null!;
```

3. Configure in `OnModelCreating`:
```csharp
modelBuilder.Entity<MyEntity>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Name).IsRequired();
});
```

4. Create migration:
```bash
dotnet ef migrations add AddMyEntity
dotnet ef database update
```

#### Adding a New API Endpoint

1. Create DTO in `Models/`:
```csharp
public class MyEntityDto
{
    public long Id { get; set; }
    public required string Name { get; set; }
}
```

2. Add controller action in `Controllers/`:
```csharp
[HttpGet("{id:long}")]
public async Task<ActionResult<MyEntityDto>> GetMyEntity(long id)
{
    var entity = await _db.MyEntities.FindAsync(id);
    if (entity == null)
        return NotFound();
    
    return Ok(MapToDto(entity));
}
```

3. Add corresponding frontend call in React component:
```jsx
const response = await apiClient.get(`/myentities/${id}`);
setEntity(response.data);
```

#### Adding a New Service

1. Create interface in `Services/`:
```csharp
public interface IMyService
{
    Task<string> DoSomethingAsync(string input);
}
```

2. Implement in `Services/`:
```csharp
public class MyService : IMyService
{
    private readonly ILogger<MyService> _logger;
    
    public MyService(ILogger<MyService> logger)
    {
        _logger = logger;
    }
    
    public async Task<string> DoSomethingAsync(string input)
    {
        _logger.LogInformation("Processing: {Input}", input);
        return await Task.FromResult($"Processed: {input}");
    }
}
```

3. Register in `Program.cs`:
```csharp
builder.Services.AddScoped<IMyService, MyService>();
```

4. Inject and use in controller:
```csharp
public class MyController : ControllerBase
{
    private readonly IMyService _myService;
    
    public MyController(IMyService myService)
    {
        _myService = myService;
    }
}
```

### Testing Workflow

1. **Test backend in isolation**:
```bash
cd src/Paparr.API
dotnet run

# In another terminal
curl http://localhost:5000/api/imports
```

2. **Test frontend in isolation**:
```bash
cd src/Paparr.UI
npm run dev

# Open http://localhost:5173
```

3. **Test together**:
```bash
# Terminal 1: Backend
cd src/Paparr.API
dotnet run

# Terminal 2: Frontend  
cd src/Paparr.UI
npm run dev

# Terminal 3: Monitor logs
docker-compose logs -f paparr-api
```

### Debugging

#### Backend Debugging in VS Code
1. Install C# extension
2. Create `.vscode/launch.json`:
```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": ".NET Core Launch (web)",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build",
            "program": "${workspaceFolder}/src/Paparr.API/bin/Debug/net8.0/Paparr.API.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/Paparr.API",
            "stopAtEntry": false,
            "serverReadyAction": {
                "pattern": "\\bNow listening on:\\s+(https?://\\S+)",
                "uriFormat": "{0}",
                "action": "openExternally"
            }
        }
    ]
}
```

3. Press F5 to start debugging

#### Frontend Debugging
1. Press F12 in browser to open DevTools
2. Set breakpoints in React components
3. Use React DevTools extension

### Code Style & Standards

#### C# Style
- Use nullable reference types (`#nullable enable`)
- Use records for DTOs when appropriate
- Use async/await consistently
- Log important operations
- Validate input parameters

#### React Style
- Use functional components with hooks
- Use meaningful component names
- Extract reusable logic into custom hooks
- Prop validation
- Handle loading and error states

### Performance Optimization

#### Backend
- Use `.AsNoTracking()` for read-only queries
- Batch database operations
- Cache frequently accessed data
- Use connection pooling

#### Frontend
- Lazy load pages/components
- Memoize expensive computations
- Use React.memo for expensive renders
- Debounce API calls on search

### Common Issues & Solutions

#### "Entity Framework Migrations Not Found"
```bash
cd src/Paparr.API
dotnet ef migrations list
dotnet ef database update
```

#### "CORS Error in Frontend"
- Check `AllowedOrigins` in `appsettings.json`
- Ensure origin includes protocol (http:// or https://)
- Restart API after changes

#### "Database Connection Failed"
```bash
# Check PostgreSQL is running
pg_isready

# Verify connection string in appsettings.json
# Test connection manually
psql -h localhost -U postgres -d paparr
```

#### "Port Already in Use"
```bash
# Find process using port 5000
netstat -ano | findstr :5000

# Kill process
taskkill /PID <PID> /F

# Or change port in appsettings.json
```

#### "Node modules version conflict"
```bash
cd src/Paparr.UI
rm -rf node_modules package-lock.json
npm install
```

### Useful Commands

**Backend:**
```bash
# List migrations
dotnet ef migrations list

# Add migration
dotnet ef migrations add MigrationName

# Remove last migration
dotnet ef migrations remove

# Update database
dotnet ef database update

# Drop database
dotnet ef database drop

# View SQL generated
dotnet ef migrations script
```

**Frontend:**
```bash
# Install dependencies
npm install

# Dev server
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview
```

**Docker:**
```bash
# Build and start
docker-compose up --build

# Start in background
docker-compose up -d

# View logs
docker-compose logs -f

# Stop
docker-compose down

# Full cleanup
docker-compose down -v
```

### Git Workflow

1. Create feature branch:
```bash
git checkout -b feature/descriptive-name
```

2. Commit with clear messages:
```bash
git commit -m "feat: add new metadata source"
git commit -m "fix: correct file hash calculation"
```

3. Push and create pull request:
```bash
git push origin feature/descriptive-name
```

### Documentation Standards

- Document public methods with XML comments
- Keep README up-to-date
- Document environment variables
- Include examples in API documentation
- Add troubleshooting tips for common issues

---

Happy developing! 🚀
