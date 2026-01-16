# Paparr Deployment Guide

## Docker Deployment (Recommended)

### Prerequisites
- Docker and Docker Compose installed
- 2GB free disk space for volumes
- Ports 5000 (API) and 5173 (UI) available

### Quick Start

1. **Clone/navigate to repository**:
```bash
cd Paparr
```

2. **Start services**:
```bash
docker-compose up -d --build
```

3. **Verify services are running**:
```bash
docker-compose ps
```

You should see:
```
paparr-postgres    ✓ healthy
paparr-api         ✓ running
paparr-ui          ✓ running
```

4. **Access the application**:
- UI: http://localhost:5173
- API: http://localhost:5000/api
- Swagger: http://localhost:5000/swagger (development only)

### Configuration for Production

1. **Update docker-compose.yml**:

```yaml
services:
  postgres:
    environment:
      POSTGRES_PASSWORD: <strong-password-here>  # Change this!
  
  paparr-api:
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=paparr;Username=postgres;Password=<strong-password>"
      ASPNETCORE_ENVIRONMENT: "Production"
      AllowedOrigins: "https://your-domain.com"  # Change this!
```

2. **Use environment file** (.env):
```bash
# .env
POSTGRES_PASSWORD=your-strong-password
ASPNETCORE_ENVIRONMENT=Production
ALLOWED_ORIGINS=https://your-domain.com
```

Then reference in docker-compose.yml:
```yaml
environment:
  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
```

3. **Start with environment file**:
```bash
docker-compose --env-file .env up -d
```

### Volume Management

The docker-compose.yml creates three volumes:

- **postgres-data**: PostgreSQL database persistence
- **ingest**: Directory for new book files
- **library**: Organized library structure

#### Accessing volumes

Mount them to your host machine:

```yaml
volumes:
  paparr-api:
    - ./ingest:/ingest
    - ./library:/library
    - postgres-data:/var/lib/postgresql/data
```

#### Backing up database

```bash
# Create backup
docker-compose exec postgres pg_dump -U postgres paparr > backup.sql

# Restore backup
docker-compose exec -T postgres psql -U postgres paparr < backup.sql
```

### Monitoring & Logs

```bash
# View all logs
docker-compose logs

# Follow API logs
docker-compose logs -f paparr-api

# Follow database logs
docker-compose logs -f postgres

# View last 100 lines
docker-compose logs --tail=100 paparr-api
```

### Troubleshooting Docker Deployment

#### Database won't connect
```bash
# Check postgres is healthy
docker-compose ps postgres

# View postgres logs
docker-compose logs postgres

# Restart postgres
docker-compose restart postgres
docker-compose exec paparr-api dotnet Paparr.API.dll
```

#### API health check
```bash
# Check if API is responding
curl http://localhost:5000/health

# View detailed logs
docker-compose logs paparr-api
```

#### Clear and rebuild
```bash
# Stop all services
docker-compose down

# Remove volumes (warning: deletes data)
docker-compose down -v

# Rebuild
docker-compose up --build -d
```

---

## Reverse Proxy Setup (Nginx/Apache)

### Nginx Configuration

```nginx
upstream paparr_api {
    server paparr-api:5000;
}

upstream paparr_ui {
    server paparr-ui:5173;
}

server {
    listen 80;
    server_name paparr.example.com;

    # Redirect HTTP to HTTPS
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name paparr.example.com;

    ssl_certificate /etc/letsencrypt/live/paparr.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/paparr.example.com/privkey.pem;

    # API endpoints
    location /api/ {
        proxy_pass http://paparr_api;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # UI
    location / {
        proxy_pass http://paparr_ui;
        proxy_set_header Host $host;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

### Update docker-compose.yml for reverse proxy

```yaml
paparr-api:
  environment:
    AllowedOrigins: "https://paparr.example.com"

paparr-ui:
  environment:
    VITE_API_URL: "https://paparr.example.com/api"
```

---

## Production Checklist

- [ ] Change default database password
- [ ] Set strong `AllowedOrigins` in API config
- [ ] Configure HTTPS/SSL certificates
- [ ] Set up reverse proxy (Nginx/Apache)
- [ ] Enable automated backups for database
- [ ] Configure logging aggregation (ELK, Datadog, etc.)
- [ ] Set up monitoring and alerting
- [ ] Implement authentication (JWT tokens)
- [ ] Enable rate limiting
- [ ] Test disaster recovery procedures
- [ ] Document deployment process
- [ ] Set up CI/CD pipeline

---

## Scaling Considerations

### Current Architecture
- Single API instance
- Single UI instance
- Single PostgreSQL instance

### For High Availability

1. **API Load Balancing**:
```yaml
services:
  paparr-api-1:
    ...
  paparr-api-2:
    ...
  paparr-api-3:
    ...
  
  nginx:
    image: nginx:latest
    ports:
      - "5000:5000"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf
```

2. **Database Replication**:
- Use PostgreSQL streaming replication
- Set up backup instances
- Configure automatic failover

3. **Distributed Background Worker**:
```yaml
paparr-worker-1:
  image: paparr-api
  command: ["dotnet", "Paparr.API.dll", "--worker-only"]
  depends_on:
    - postgres

paparr-worker-2:
  image: paparr-api
  command: ["dotnet", "Paparr.API.dll", "--worker-only"]
  depends_on:
    - postgres
```

---

## Kubernetes Deployment (Advanced)

### Basic Kubernetes Manifests

```yaml
# paparr-api-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: paparr-api
spec:
  replicas: 3
  selector:
    matchLabels:
      app: paparr-api
  template:
    metadata:
      labels:
        app: paparr-api
    spec:
      containers:
      - name: api
        image: paparr-api:latest
        ports:
        - containerPort: 5000
        env:
        - name: ConnectionStrings__DefaultConnection
          valueFrom:
            secretKeyRef:
              name: paparr-secrets
              key: db-connection
        resources:
          requests:
            memory: "256Mi"
            cpu: "250m"
          limits:
            memory: "512Mi"
            cpu: "500m"
```

```yaml
# paparr-service.yaml
apiVersion: v1
kind: Service
metadata:
  name: paparr-api
spec:
  selector:
    app: paparr-api
  ports:
  - protocol: TCP
    port: 5000
    targetPort: 5000
  type: LoadBalancer
```

Deploy:
```bash
kubectl apply -f paparr-api-deployment.yaml
kubectl apply -f paparr-service.yaml
```

---

## Monitoring & Maintenance

### Log Aggregation

Enable Serilog sinks for:
- **Seq** (development/small scale)
- **ELK Stack** (Elasticsearch, Logstash, Kibana)
- **Datadog** (production SaaS)
- **Splunk** (enterprise)

Update `Program.cs`:
```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.Seq("http://seq:5341")  // Add Seq sink
    .CreateLogger();
```

### Health Checks

Add to API:
```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

// In Program.cs
app.MapHealthChecks("/health");
```

### Metrics

Use Prometheus:
```csharp
builder.Services.AddPrometheusActuatorServices();

// In Program.cs
app.UsePrometheusActuator();
```

---

## Disaster Recovery

### Automated Backups

```bash
#!/bin/bash
# backup.sh
BACKUP_DIR="/backups/paparr"
DATE=$(date +%Y%m%d_%H%M%S)

# Backup database
docker-compose exec postgres pg_dump -U postgres paparr | gzip > $BACKUP_DIR/paparr_$DATE.sql.gz

# Backup library
tar -czf $BACKUP_DIR/library_$DATE.tar.gz ./library

# Keep last 30 days
find $BACKUP_DIR -name "paparr_*.sql.gz" -mtime +30 -delete
```

Schedule with cron:
```bash
0 2 * * * /path/to/backup.sh  # Daily at 2 AM
```

### Restore Procedure

```bash
# 1. Stop services
docker-compose down

# 2. Drop old database
docker-compose exec postgres dropdb -U postgres paparr

# 3. Restore backup
gunzip < /backups/paparr/paparr_20240116_020000.sql.gz | \
  docker-compose exec -T postgres psql -U postgres

# 4. Restore library
tar -xzf /backups/paparr/library_20240116_020000.tar.gz

# 5. Start services
docker-compose up -d
```

---

## Support & Troubleshooting

### Common Deployment Issues

**Port conflicts:**
```bash
# Find process using port
sudo lsof -i :5000
# Change port in docker-compose.yml
```

**Out of disk space:**
```bash
# Clean up Docker
docker system prune -a --volumes

# Compress database
docker-compose exec postgres vacuumdb -U postgres paparr
```

**Performance degradation:**
```bash
# Check database size
docker-compose exec postgres psql -U postgres paparr -c "SELECT pg_size_pretty(pg_database_size('paparr'));"

# Rebuild indexes
docker-compose exec postgres psql -U postgres paparr -c "REINDEX DATABASE paparr;"
```

---

For additional support, refer to:
- [Docker Compose Documentation](https://docs.docker.com/compose/)
- [PostgreSQL Documentation](https://www.postgresql.org/docs/)
- [ASP.NET Core Deployment](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/)
- [React Deployment](https://react.dev/learn/deployment)
