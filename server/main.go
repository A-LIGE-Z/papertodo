package main

import (
	"crypto/sha256"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

var (
	portFlag    = flag.Int("port", 8080, "HTTP server listening port")
	hostFlag    = flag.String("host", "127.0.0.1", "HTTP server listening host")
	dataDirFlag = flag.String("data-dir", "./data", "Directory to store sync data")
	tokenFlag   = flag.String("token", "", "Pre-shared authorization token (Bearer)")
	mutex       sync.RWMutex
)

func main() {
	flag.Parse()

	// Environment variable overrides
	if envPort := os.Getenv("PAPERTODO_PORT"); envPort != "" {
		fmt.Sscanf(envPort, "%d", portFlag)
	}
	if envHost := os.Getenv("PAPERTODO_HOST"); envHost != "" {
		*hostFlag = envHost
	}
	if envData := os.Getenv("PAPERTODO_DATA_DIR"); envData != "" {
		*dataDirFlag = envData
	}
	if envToken := os.Getenv("PAPERTODO_TOKEN"); envToken != "" {
		*tokenFlag = envToken
	}
	if strings.TrimSpace(*tokenFlag) == "" {
		log.Fatal("A non-empty -token or PAPERTODO_TOKEN is required. Configure HTTPS before exposing this server remotely.")
	}

	if err := os.MkdirAll(*dataDirFlag, 0700); err != nil {
		log.Fatalf("Failed to create data directory: %v", err)
	}

	backupDir := filepath.Join(*dataDirFlag, "backups")
	if err := os.MkdirAll(backupDir, 0700); err != nil {
		log.Fatalf("Failed to create backups directory: %v", err)
	}

	http.HandleFunc("/api/v1/ping", authMiddleware(handlePing))
	http.HandleFunc("/api/v1/sync", authMiddleware(handleSync))
	http.HandleFunc("/api/v1/backup/export", authMiddleware(handleExportBackup))

	addr := fmt.Sprintf("%s:%d", *hostFlag, *portFlag)
	log.Printf("==================================================")
	log.Printf(" PaperTodo Standalone Sync Server (No Docker Needed)")
	log.Printf(" Listening on: http://%s", addr)
	log.Printf(" Data directory: %s", *dataDirFlag)
	if *tokenFlag != "" {
		log.Printf(" Auth Token: Enabled (Bearer)")
	} else {
		log.Printf(" Auth Token: WARNING! None set. Anyone can read/write data.")
	}
	log.Printf("==================================================")

	server := &http.Server{Addr: addr, ReadHeaderTimeout: 10 * time.Second, ReadTimeout: 60 * time.Second,
		WriteTimeout: 60 * time.Second, IdleTimeout: 60 * time.Second}
	if err := server.ListenAndServe(); err != nil {
		log.Fatalf("Server exited with error: %v", err)
	}
}

func authMiddleware(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if *tokenFlag != "" {
			authHeader := r.Header.Get("Authorization")
			expectedToken := *tokenFlag
			token := ""
			if strings.HasPrefix(authHeader, "Bearer ") {
				token = strings.TrimPrefix(authHeader, "Bearer ")
			} else {
				token = authHeader
			}

			if token != expectedToken {
				http.Error(w, `{"error": "Unauthorized"}`, http.StatusUnauthorized)
				return
			}
		}
		next(w, r)
	}
}

func handlePing(w http.ResponseWriter, r *http.Request) {
	probe, err := os.CreateTemp(*dataDirFlag, ".connection-check-")
	if err != nil {
		http.Error(w, `{"error":"Storage directory is not writable"}`, http.StatusServiceUnavailable)
		return
	}
	probe.Close()
	os.Remove(probe.Name())
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	w.Write([]byte(`{"status":"ok","app":"PaperTodo.SyncServer","version":"0.0.1"}`))
}

func getDataFilePath() string {
	return filepath.Join(*dataDirFlag, "data.json")
}

func handleSync(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		mutex.RLock()
		defer mutex.RUnlock()

		filePath := getDataFilePath()
		if _, err := os.Stat(filePath); os.IsNotExist(err) {
			http.Error(w, `{"error": "File not found"}`, http.StatusNotFound)
			return
		}

		data, err := os.ReadFile(filePath)
		if err != nil {
			http.Error(w, "Could not read data", http.StatusInternalServerError)
			return
		}
		w.Header().Set("ETag", revision(data))
		w.Header().Set("Content-Type", "application/json; charset=utf-8")
		w.Write(data)

	case http.MethodPost, http.MethodPut:
		r.Body = http.MaxBytesReader(w, r.Body, 32<<20)
		body, err := io.ReadAll(r.Body)
		if err != nil {
			http.Error(w, fmt.Sprintf(`{"error": "%v"}`, err), http.StatusBadRequest)
			return
		}

		body, err = sharedDocument(body)
		if err != nil {
			http.Error(w, `{"error": "Invalid JSON payload"}`, http.StatusBadRequest)
			return
		}

		mutex.Lock()
		defer mutex.Unlock()

		filePath := getDataFilePath()
		previous, readErr := os.ReadFile(filePath)
		exists := readErr == nil
		if readErr != nil && !os.IsNotExist(readErr) {
			http.Error(w, "Could not read existing data", http.StatusInternalServerError)
			return
		}
		if expected := r.Header.Get("If-Match"); expected != "" && (!exists || expected != revision(previous)) {
			http.Error(w, "Data changed; download and merge again", http.StatusPreconditionFailed)
			return
		}
		if r.Header.Get("If-None-Match") == "*" && exists {
			http.Error(w, "Data already exists; download and merge again", http.StatusPreconditionFailed)
			return
		}

		// If file exists, create a rolling backup before overwriting
		if _, err := os.Stat(filePath); err == nil {
			timestamp := time.Now().UTC().Format("20060102-150405.000000000")
			backupFile := filepath.Join(*dataDirFlag, "backups", fmt.Sprintf("data-%s.json", timestamp))
			if err := copyFile(filePath, backupFile); err != nil {
				http.Error(w, "Backup failed; existing data was preserved", http.StatusInternalServerError)
				return
			}
			cleanOldBackups(filepath.Join(*dataDirFlag, "backups"), 30)
		}

		// Atomic write
		tmpPath := filePath + ".tmp"
		if err := writeFlushed(tmpPath, body); err != nil {
			http.Error(w, fmt.Sprintf(`{"error": "Failed to write file: %v"}`, err), http.StatusInternalServerError)
			return
		}

		if err := os.Rename(tmpPath, filePath); err != nil {
			http.Error(w, fmt.Sprintf(`{"error": "Failed to rename file: %v"}`, err), http.StatusInternalServerError)
			return
		}

		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("ETag", revision(body))
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"success": true, "message": "Synced successfully"}`))

	default:
		http.Error(w, `{"error": "Method not allowed"}`, http.StatusMethodNotAllowed)
	}
}

func revision(data []byte) string { return fmt.Sprintf(`"%x"`, sha256.Sum256(data)) }

func sharedDocument(body []byte) ([]byte, error) {
	var root map[string]json.RawMessage
	if err := json.Unmarshal(body, &root); err != nil {
		return nil, err
	}
	var papers []map[string]json.RawMessage
	if root["papers"] == nil || string(root["papers"]) == "null" {
		return nil, fmt.Errorf("papers array required")
	}
	if err := json.Unmarshal(root["papers"], &papers); err != nil {
		return nil, err
	}
	ids := map[string]bool{}
	for _, paper := range papers {
		var id string
		if json.Unmarshal(paper["id"], &id) != nil || id == "" || ids[id] {
			return nil, fmt.Errorf("invalid paper ID")
		}
		ids[id] = true
	}
	tombstones := root["syncTombstones"]
	if tombstones == nil {
		tombstones = json.RawMessage(`[]`)
	}
	var records []json.RawMessage
	if err := json.Unmarshal(tombstones, &records); err != nil {
		return nil, err
	}
	// Also sanitize older clients that still upload their complete local settings.
	return json.Marshal(map[string]json.RawMessage{"schemaVersion": json.RawMessage(`1`), "papers": root["papers"], "syncTombstones": tombstones})
}

func writeFlushed(path string, data []byte) error {
	file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0600)
	if err != nil {
		return err
	}
	if _, err = file.Write(data); err == nil {
		err = file.Sync()
	}
	closeErr := file.Close()
	if err != nil {
		return err
	}
	return closeErr
}

func handleExportBackup(w http.ResponseWriter, r *http.Request) {
	mutex.RLock()
	defer mutex.RUnlock()

	filePath := getDataFilePath()
	if _, err := os.Stat(filePath); os.IsNotExist(err) {
		http.Error(w, `{"error": "No backup available"}`, http.StatusNotFound)
		return
	}

	w.Header().Set("Content-Disposition", "attachment; filename=data.backup.json")
	w.Header().Set("Content-Type", "application/json")
	http.ServeFile(w, r, filePath)
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()

	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	defer out.Close()

	_, err = io.Copy(out, in)
	return err
}

func cleanOldBackups(dir string, maxKeep int) {
	files, err := os.ReadDir(dir)
	if err != nil || len(files) <= maxKeep {
		return
	}

	// Remove older files
	excess := len(files) - maxKeep
	for i := 0; i < excess; i++ {
		_ = os.Remove(filepath.Join(dir, files[i].Name()))
	}
}
