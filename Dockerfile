FROM python:3.14-slim

WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

# Run as an unprivileged user so files written into the bind-mounted
# data/ (positions DB, password hash) are not root-owned on the host.
# On Docker Desktop for macOS this just works; on a Linux host, chown
# data/ and export/ to uid 1000 first.
RUN groupadd --gid 1000 app \
    && useradd --uid 1000 --gid app --create-home app \
    && chown -R app:app /app
USER app

# Binds to all interfaces inside the container; nginx is the only
# external entry point (see nginx.conf).
EXPOSE 8000
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
