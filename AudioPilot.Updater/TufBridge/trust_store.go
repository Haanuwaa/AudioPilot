package main

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/theupdateframework/go-tuf/v2/metadata"
	"github.com/theupdateframework/go-tuf/v2/metadata/updater"
	"golang.org/x/sys/windows"
)

const journalLimit = 64 * 1024 * 1024
const recordLimit = 196608
const journalCommit uint32 = 0x41505431

func cleanWorkingCaches(cache string) error {
	entries, err := os.ReadDir(cache)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if !strings.HasPrefix(entry.Name(), "work-") {
			continue
		}
		path := filepath.Join(cache, entry.Name())
		count := 0
		err = filepath.WalkDir(path, func(current string, item fs.DirEntry, walkErr error) error {
			if walkErr != nil {
				return walkErr
			}
			count++
			if count > 32 {
				return errors.New("oversized abandoned TUF working cache")
			}
			if _, e := safePath(current); e != nil {
				return e
			}
			if current != path && (item.IsDir() || !strings.HasSuffix(item.Name(), ".json") || !validRole(strings.TrimSuffix(item.Name(), ".json"))) {
				return errors.New("unexpected abandoned TUF cache entry")
			}
			return nil
		})
		if err != nil {
			return err
		}
		if err = os.RemoveAll(path); err != nil {
			return err
		}
	}
	return nil
}

type trustRecord struct {
	Sequence int64  `json:"sequence"`
	Role     string `json:"role"`
	Data     []byte `json:"data"`
	Previous string `json:"previous"`
}
type trustStore struct {
	file     *os.File
	latest   map[string][]byte
	sequence int64
	last     string
	size     int64
	fatal    error
	// Test-only fault boundary; never configured from IPC or the environment.
	fault func(string) error
}

func openTrustStore(cache, pin string, bootstrap []byte) (*trustStore, error) {
	anchorPath := cache + ".anchor"
	if _, err := safePath(anchorPath); err != nil {
		return nil, err
	}
	anchor, anchorErr := boundedRead(anchorPath, 128)
	anchored := anchorErr == nil
	if anchorErr != nil && !os.IsNotExist(anchorErr) {
		return nil, anchorErr
	}
	if anchored && string(anchor) != pin {
		return nil, errors.New("TUF cache anchor belongs to another bootstrap")
	}
	_, err := os.Stat(cache)
	fresh := os.IsNotExist(err)
	if err != nil && !fresh {
		return nil, err
	}
	if fresh && anchored {
		return nil, errors.New("initialized TUF cache directory is missing")
	}
	if fresh {
		if err = os.MkdirAll(cache, 0700); err != nil {
			return nil, err
		}
	}
	if err = protectTrustDirectory(cache); err != nil {
		return nil, err
	}
	path := filepath.Join(cache, "trust.journal")
	if _, err = safePath(path); err != nil {
		return nil, err
	}
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	disposition := uint32(windows.OPEN_EXISTING)
	if fresh {
		disposition = windows.CREATE_NEW
	}
	handle, err := windows.CreateFile(name, windows.GENERIC_READ|windows.GENERIC_WRITE, 0, nil, disposition, windows.FILE_ATTRIBUTE_NORMAL|windows.FILE_FLAG_WRITE_THROUGH, 0)
	if err != nil {
		return nil, err
	}
	s := &trustStore{file: os.NewFile(uintptr(handle), path), latest: map[string][]byte{}}
	ok := false
	defer func() {
		if !ok {
			s.file.Close()
		}
	}()
	header := []byte("AudioPilot TUF trust journal 1\n" + pin + "\n")
	if fresh {
		if _, err = s.file.Write(header); err != nil {
			return nil, err
		}
		if err = s.file.Sync(); err != nil {
			return nil, err
		}
		s.size = int64(len(header))
		if err = s.append("root", bootstrap); err != nil {
			return nil, err
		}
	} else {
		if err = s.replay(header, bootstrap); err != nil {
			return nil, err
		}
	}
	if !anchored {
		if s.sequence != 1 || !bytes.Equal(s.latest["root"], bootstrap) {
			return nil, errors.New("initialized TUF cache anchor is missing")
		}
		if err = writeCacheAnchor(anchorPath, []byte(pin)); err != nil {
			return nil, err
		}
	}
	ok = true
	return s, nil
}
func writeCacheAnchor(path string, data []byte) error {
	if _, err := safePath(path + ".new"); err != nil {
		return err
	}
	file, err := os.OpenFile(path+".new", os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
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
	if closeErr != nil {
		return closeErr
	}
	if err = protectTrustDirectory(path + ".new"); err != nil {
		return err
	}
	source, err := windows.UTF16PtrFromString(path + ".new")
	if err != nil {
		return err
	}
	destination, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	return windows.MoveFileEx(source, destination, windows.MOVEFILE_REPLACE_EXISTING|windows.MOVEFILE_WRITE_THROUGH)
}
func protectTrustDirectory(path string) error {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return err
	}
	descriptor, err := windows.SecurityDescriptorFromString("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + user.User.Sid.String() + ")")
	if err != nil {
		return err
	}
	dacl, _, err := descriptor.DACL()
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil)
}
func (s *trustStore) replay(header, bootstrap []byte) error {
	info, err := s.file.Stat()
	if err != nil {
		return err
	}
	if info.Size() > journalLimit {
		return errors.New("TUF trust journal exceeds its bound")
	}
	reader := bufio.NewReader(s.file)
	actual := make([]byte, len(header))
	if _, err = io.ReadFull(reader, actual); err != nil || !bytes.Equal(header, actual) {
		return errors.New("missing or corrupt TUF journal authority")
	}
	s.size = int64(len(header))
	for {
		prefix := make([]byte, 8)
		n, readErr := io.ReadFull(reader, prefix)
		if readErr == io.EOF && n == 0 {
			break
		}
		if readErr == io.ErrUnexpectedEOF {
			return s.trimTail()
		}
		if readErr != nil {
			return readErr
		}
		length := binary.LittleEndian.Uint32(prefix)
		if length == 0 || length > recordLimit || binary.LittleEndian.Uint32(prefix[4:]) != ^length {
			return errors.New("corrupt TUF journal frame")
		}
		body := make([]byte, int(length)+36)
		if _, err = io.ReadFull(reader, body); err == io.EOF || err == io.ErrUnexpectedEOF {
			return s.trimTail()
		} else if err != nil {
			return err
		}
		sum := sha256.Sum256(body[:length])
		if !bytes.Equal(sum[:], body[length:length+32]) || binary.LittleEndian.Uint32(body[length+32:]) != journalCommit {
			return errors.New("corrupt committed TUF trust record")
		}
		var record trustRecord
		d := json.NewDecoder(bytes.NewReader(body[:length]))
		d.DisallowUnknownFields()
		if err = d.Decode(&record); err != nil {
			return err
		}
		if record.Sequence != s.sequence+1 || record.Previous != s.last || !validRole(record.Role) || len(record.Data) == 0 || len(record.Data) > 131072 {
			return errors.New("invalid TUF journal chain")
		}
		if s.sequence == 0 && (record.Role != "root" || !bytes.Equal(record.Data, bootstrap)) {
			return errors.New("TUF journal has no pinned bootstrap")
		}
		s.latest[record.Role] = record.Data
		s.sequence = record.Sequence
		s.last = hex.EncodeToString(sum[:])
		s.size += 8 + int64(len(body))
	}
	if s.sequence == 0 {
		return errors.New("TUF journal initialization did not complete")
	}
	_, err = s.file.Seek(s.size, io.SeekStart)
	return err
}
func (s *trustStore) trimTail() error {
	if s.sequence == 0 {
		return errors.New("incomplete initial TUF trust record")
	}
	if err := s.file.Truncate(s.size); err != nil {
		return err
	}
	if err := s.file.Sync(); err != nil {
		return err
	}
	_, err := s.file.Seek(s.size, io.SeekStart)
	return err
}
func validRole(role string) bool {
	if role == "" || len(role) > 100 || strings.Contains(role, "..") {
		return false
	}
	for _, c := range role {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_') {
			return false
		}
	}
	return true
}
func (s *trustStore) append(role string, data []byte) (err error) {
	if s.fatal != nil {
		return s.fatal
	}
	defer func() {
		if err != nil {
			s.fatal = err
		}
	}()
	if bytes.Equal(s.latest[role], data) {
		return nil
	}
	if !validRole(role) || len(data) > 131072 {
		return errors.New("unsupported TUF trust role")
	}
	encoded, err := json.Marshal(trustRecord{s.sequence + 1, role, data, s.last})
	if err != nil {
		return err
	}
	if len(encoded) > recordLimit || s.size+int64(len(encoded))+44 > journalLimit {
		return errors.New("TUF trust journal is full")
	}
	sum := sha256.Sum256(encoded)
	frame := make([]byte, len(encoded)+44)
	binary.LittleEndian.PutUint32(frame, uint32(len(encoded)))
	binary.LittleEndian.PutUint32(frame[4:], ^uint32(len(encoded)))
	copy(frame[8:], encoded)
	copy(frame[8+len(encoded):], sum[:])
	binary.LittleEndian.PutUint32(frame[len(frame)-4:], journalCommit)
	if s.fault != nil {
		if err = s.fault("before-write"); err != nil {
			return err
		}
	}
	if _, err = s.file.Write(frame); err != nil {
		return err
	}
	if s.fault != nil {
		if err = s.fault("before-sync"); err != nil {
			return err
		}
	}
	if err = s.file.Sync(); err != nil {
		return err
	}
	if s.fault != nil {
		if err = s.fault("after-sync"); err != nil {
			return err
		}
	}
	s.sequence++
	s.last = hex.EncodeToString(sum[:])
	s.size += int64(len(frame))
	s.latest[role] = append([]byte(nil), data...)
	return nil
}

type roleCandidate struct{ raw, canonical []byte }
type trustCheckpoint struct {
	store      *trustStore
	client     *updater.Updater
	candidates map[string][]roleCandidate
}

func newTrustCheckpoint(store *trustStore) (*trustCheckpoint, error) {
	c := &trustCheckpoint{store: store, candidates: map[string][]roleCandidate{}}
	for role, data := range store.latest {
		if err := c.remember(role, data); err != nil {
			return nil, err
		}
	}
	return c, nil
}
func canonicalRole(role string, raw []byte) ([]byte, error) {
	switch role {
	case "root":
		m, e := metadata.Root().FromBytes(raw)
		if e != nil {
			return nil, e
		}
		return m.ToBytes(false)
	case "timestamp":
		m, e := metadata.Timestamp().FromBytes(raw)
		if e != nil {
			return nil, e
		}
		return m.ToBytes(false)
	case "snapshot":
		m, e := metadata.Snapshot().FromBytes(raw)
		if e != nil {
			return nil, e
		}
		return m.ToBytes(false)
	default:
		m, e := metadata.Targets().FromBytes(raw)
		if e != nil {
			return nil, e
		}
		return m.ToBytes(false)
	}
}
func (c *trustCheckpoint) remember(role string, raw []byte) error {
	if !validRole(role) {
		return errors.New("unsupported delegated role name")
	}
	canonical, err := canonicalRole(role, raw)
	if err != nil {
		return err
	}
	items := append(c.candidates[role], roleCandidate{raw, canonical})
	if len(items) > 2 {
		items = items[len(items)-2:]
	}
	c.candidates[role] = items
	return nil
}
func (c *trustCheckpoint) capture() error {
	if c.client == nil {
		return nil
	}
	state := c.client.GetTrustedMetadataSet()
	type document interface{ ToBytes(bool) ([]byte, error) }
	documents := map[string]document{"root": state.Root}
	if state.Timestamp != nil {
		documents["timestamp"] = state.Timestamp
	}
	if state.Snapshot != nil {
		documents["snapshot"] = state.Snapshot
	}
	for name, targets := range state.Targets {
		if targets != nil {
			documents[name] = targets
		}
	}
	roles := []string{"root", "timestamp", "snapshot"}
	for name := range state.Targets {
		roles = append(roles, name)
	}
	sort.Strings(roles[3:])
	for _, role := range roles {
		doc, ok := documents[role]
		if !ok {
			continue
		}
		canonical, err := doc.ToBytes(false)
		if err != nil {
			return err
		}
		found := false
		for _, candidate := range c.candidates[role] {
			if bytes.Equal(candidate.canonical, canonical) {
				if err = c.store.append(role, candidate.raw); err != nil {
					return err
				}
				found = true
				break
			}
		}
		if !found {
			return fmt.Errorf("verified %s metadata has no exact source bytes", role)
		}
	}
	return nil
}
