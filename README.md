# ProxyLight

A very simple app to proxy requests to any provided URL.

The app will make a request to the requested URL. The body and `Content-Type` header from the response to that request, is copied to the output response stream, without doing any further processing.
Currently, only GET requests are supported.

To use it, send a request to the app, with the query parameter `u` set to the desired URL.  
E.g.: `http://localhost:5000/?u=https%3A%2F%2Fexample.com`

Build and run using these commands from src folder:

```sh
docker build -t proxy .
docker run -t --rm -p 5000:5000 -v ./cache:/app/cache -e PROXYLIGHT__CACHE__ENABLED=true --name proxy proxy:latest
```

## Docker image

To make the image as small as possible, a "distroless" base image is used, provided by Microsoft.  
See: <https://github.com/dotnet/dotnet-docker/blob/main/documentation/distroless.md>

Resulting image is about 45 MB in size.


## TODO

Things that should be documented or features that should be added.

- CORS
- Logging
- Other request types
- Modify proxy request (add/remove headers, etc.)
- Use stream instead of reading byte array to memory
- Caching
- Forwarding / customizing request headers (e.g. User-Agent)
- Blacklisting and whitelisting domains (blocking LAN requests)
