FROM golang:1.27-alpine AS build
WORKDIR /src
COPY server/go.mod ./
COPY server/*.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /papertodo-server .

FROM alpine:3.22
RUN apk --no-cache add ca-certificates
WORKDIR /app
COPY --from=build /papertodo-server /app/papertodo-server
RUN chmod +x /app/papertodo-server
EXPOSE 8080
VOLUME ["/app/data"]
ENTRYPOINT ["/app/papertodo-server"]
