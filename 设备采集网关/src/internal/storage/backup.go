package storage

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
)

// Does not initialize or migrate the source database. VACUUM INTO includes WAL
// committed data in a consistent SQLite snapshot, unlike copying only the .db.
func BackupDatabase(path, dest string) error {
	if _, err := os.Stat(path); err != nil {
		return err
	}
	if _, err := os.Stat(dest); !os.IsNotExist(err) {
		return fmt.Errorf("backup destination exists or inaccessible")
	}
	if err := os.MkdirAll(filepath.Dir(dest), 0700); err != nil {
		return err
	}
	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(path)+"?mode=ro")
	if err != nil {
		return err
	}
	defer db.Close()
	_, err = db.Exec("VACUUM INTO ?", dest)
	if err != nil {
		return err
	}
	return os.Chmod(dest, 0600)
}
