package main

import (
	"bytes"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func setup(t *testing.T) {
	t.Helper()
	*dataDirFlag = t.TempDir()
	*tokenFlag = "test-token"
	if err := os.MkdirAll(filepath.Join(*dataDirFlag, "backups"), 0700); err != nil {
		t.Fatal(err)
	}
}

func request(method, path, body, token, etag string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(method, path, bytes.NewBufferString(body))
	req.Header.Set("Authorization", "Bearer "+token)
	if etag != "" {
		req.Header.Set("If-Match", etag)
	}
	recorder := httptest.NewRecorder()
	handler := handleSync
	if path == "/api/v1/ping" {
		handler = handlePing
	}
	authMiddleware(handler)(recorder, req)
	return recorder
}

func TestPingRequiresCorrectAuthentication(t *testing.T) {
	setup(t)
	if got := request("GET", "/api/v1/ping", "", "wrong", "").Code; got != http.StatusUnauthorized {
		t.Fatal(got)
	}
	if got := request("GET", "/api/v1/ping", "", "test-token", "").Code; got != http.StatusOK {
		t.Fatal(got)
	}
}

func TestInvalidPayloadAndStaleWritesPreserveExistingData(t *testing.T) {
	setup(t)
	initial := `{"papers":[{"id":"p","content":"first"}],"syncWebDavPassword":"private"}`
	if got := request("POST", "/api/v1/sync", initial, "test-token", "").Code; got != 200 {
		t.Fatal(got)
	}
	first := request("GET", "/api/v1/sync", "", "test-token", "")
	if strings.Contains(first.Body.String(), "private") {
		t.Fatal("credentials retained")
	}
	etag := first.Header().Get("ETag")
	if etag == "" {
		t.Fatal("missing revision")
	}
	if got := request("POST", "/api/v1/sync", `{"papers":[{"id":"p","content":"new"}]}`, "test-token", etag).Code; got != 200 {
		t.Fatal(got)
	}
	if got := request("POST", "/api/v1/sync", initial, "test-token", etag).Code; got != 412 {
		t.Fatal(got)
	}
	if got := request("POST", "/api/v1/sync", `{}`, "test-token", "").Code; got != 400 {
		t.Fatal(got)
	}
	current := request("GET", "/api/v1/sync", "", "test-token", "")
	if !strings.Contains(current.Body.String(), "new") {
		t.Fatal(current.Body.String())
	}
}

func TestRapidWritesKeepDistinctBackups(t *testing.T) {
	setup(t)
	for i := 0; i < 6; i++ {
		if got := request("POST", "/api/v1/sync", `{"papers":[]}`, "test-token", "").Code; got != 200 {
			t.Fatal(got)
		}
	}
	files, err := os.ReadDir(filepath.Join(*dataDirFlag, "backups"))
	if err != nil || len(files) != 5 {
		t.Fatalf("backups=%d, err=%v", len(files), err)
	}
}
