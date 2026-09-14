# 검증 기록

작성일: 2026-09-09
빌드: .NET 10.0.401 / net10.0-windows / win-x64, 경고 0 · 오류 0
자동 테스트: **79개 전부 통과** (xUnit, 격리된 임시 폴더에서만 실행)

설계 문서 §10 검증표를 기준으로, 실제로 확인한 것과 확인하지 못한 것을 구분한다.

---

## 1. 자동 테스트로 검증 완료

| 시나리오 | 확인한 내용 | 테스트 |
|---|---|---|
| 한글·공백·대문자 확장자·중첩 폴더 | 대상 파일만 보관, `.docx`/`.png`/`.txt` 제외 | `DocumentScannerTests.Scan_finds_korean_and_nested_documents_and_ignores_everything_else` |
| Office 잠금 파일 제외 | `~$…`는 문서로 취급하지 않고 백업하지 않음 | `The_owner_file_itself_is_never_backed_up` |
| **잠금 파일 → 영구 등급** | 잠금 파일이 있는 PPT만 `retained/`로 가고 업로드 큐에 등록됨 | `An_opened_presentation_is_retained_and_queued_for_drive` |
| **안 연 PPT / PDF → 임시 등급** | `temp/`에 보관되고 업로드 큐에 **등록되지 않음** | `A_presentation_that_was_never_opened_is_only_kept_temporarily_and_never_queued`, `A_pdf_is_always_temporary_because_it_has_no_owner_file` |
| **나중에 열었을 때 승격** | 원본 재읽기 없이 같은 백업 ID가 `temp/`→`retained/`로 이동, 업로드 큐 등록 | `Opening_a_deck_later_promotes_the_existing_copy_without_reading_the_source_again` |
| **닫은 뒤에도 등급 유지** | 잠금 파일이 사라져도 이후 버전까지 영구 등급 | `A_deck_stays_retained_after_the_owner_file_disappears` |
| 잠금 파일 이름 규칙 | 짧은 이름(`~$a.pptx`)과 두 글자 잘린 이름(`~$26 학회 발표.pptx`) 양쪽 해석 | `OfficeLockFileTests` 7개 |
| 같은 내용 재탐색 / 내용 변경 | 새 버전 안 만듦 / 새 버전 생성하고 이전 버전 보존 | `Rescanning_unchanged_content_does_not_create_a_second_version`, `Changed_content_creates_a_new_version_and_keeps_the_old_one` |
| 주기 재탐색 최적화 | 크기·수정 시각이 같으면 읽지 않고 건너뜀 | `Periodic_rescan_skips_files_whose_size_and_timestamp_are_unchanged` |
| 다른 USB의 같은 파일명 | 장치별로 분리 보관, 서로 덮어쓰지 않음 | `Two_devices_with_the_same_file_name_are_archived_separately` |
| 문서 저장 중 / 접근 거부 | 배타 잠금된 파일은 실패로 기록하고 재시도, 완료 처리 안 함 | `A_file_locked_for_exclusive_writing_is_retried_rather_than_completed` |
| 디스크 여유 부족 | 복사를 시작하지 않고 원인 기록, 기존 백업 보존 | `A_full_archive_disk_does_not_produce_a_completed_backup` |
| 복사 도중 취소 (USB 분리 대용) | `Pending` 행·`.partial` 파일 남지 않음, 완료된 것은 해시 일치 | `Cancelling_mid_scan_leaves_no_pending_row_and_no_partial_file` |
| **rename 후 DB 반영 전 중단** | 시작 시 해시 대조 후 완료 처리 + 업로드 큐 등록 | `A_crash_after_the_rename_but_before_the_commit_is_completed` |
| **rename 전 중단** | 행과 `.partial` 모두 폐기 | `A_crash_before_the_rename_discards_the_unproven_copy` |
| 크기는 맞고 내용이 다른 파일 | 완료로 인정하지 않음 (메타데이터만으로 판단 안 함) | `A_renamed_file_whose_content_does_not_match_is_not_accepted` |
| 고아 `.partial` 정리 | 자기 임시 파일만 지우고 완료된 백업은 건드리지 않음 | `An_orphaned_partial_file_is_swept_but_finished_backups_are_left_alone` |
| 업로드 중 비정상 종료 | `Uploading` → `Waiting`으로 복귀 | `An_upload_left_in_flight_is_returned_to_the_queue` |
| 원본 삭제 후 복원 | 복원본 바이트 일치 + SHA-256 일치 + PPTX 패키지로 여전히 읽힘 | `A_deleted_original_can_be_restored_and_matches_the_recorded_hash` |
| 이전 버전 복원 | 최신본이 아니라 고른 버전이 복원됨 | `Restoring_an_older_version_returns_that_version_not_the_newest` |
| 같은 이름 파일 존재 | 덮어쓰지 않고 `이름 (2).pdf`로 저장, 기존 파일 내용 유지 | `Restoring_twice_keeps_the_existing_file_and_writes_a_new_name` |
| 보관본 없음 | 성공으로 위장하지 않고 사유 표시 | `A_missing_archive_file_reports_why_instead_of_claiming_success` |
| **원본 무수정** | 복사 전후 바이트·수정 시각·속성 동일 | `Backup_never_modifies_the_source` |
| **7일 정리 — 임시** | 7일 지난 임시 백업만 파일+행 삭제, 기간 내는 보존 | `A_temporary_backup_older_than_the_window_is_removed_with_its_row`, `A_temporary_backup_inside_the_window_is_kept` |
| **7일 정리 — 영구 (업로드 미완료)** | 400일이 지나도 업로드가 대기·조치 필요면 **삭제하지 않음** | `A_retained_presentation_is_never_released_while_its_upload_is_still_waiting`, `A_retained_presentation_that_needs_attention_is_also_kept` |
| **7일 정리 — 영구 (업로드 완료)** | 로컬 사본만 삭제하고 DB 행·SHA-256·Drive ID는 보존 | `A_retained_presentation_confirmed_in_drive_gives_up_its_local_copy_but_keeps_its_row` |
| 정리 루틴 오작동 방어 | 등급과 실제 폴더가 어긋난 행은 삭제 거부 + 문제 기록 | `A_temporary_row_pointing_outside_the_temporary_folder_is_refused` |
| 재분석 지점 | 디렉터리 심볼릭 링크를 따라가지 않음 | `Scan_does_not_follow_a_directory_junction` (개발자 모드 없으면 자동 skip) |

샘플 파일은 확장자만 바꾼 더미가 아니라 **구조적으로 유효한 PDF(xref 오프셋 계산)와
PPTX(OPC 패키지: `[Content_Types].xml`, 관계, 슬라이드 마스터/레이아웃/슬라이드)** 를 생성해 사용했다.

## 2. 빌드·패키징 검증 완료

- `dotnet build -c Debug` / `-c Release`: 경고 0, 오류 0 (`TreatWarningsAsErrors=true`)
- 자체 포함 단일 실행 파일 생성 성공: `artifacts/publish/DocumentBackup.exe` (약 106 MB)
- 취약점 스캔: `Microsoft.Data.Sqlite` 10.0.0이 끌어오던 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11의
  high 등급 권고(GHSA-2m69-gcr7-jv3q)를 빌드가 오류로 잡아냄 →
  `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5를 직접 고정해 해소

---

## 3. 미검증 — 실제 장치·계정·재부팅이 필요함

**모의 테스트가 통과했다고 실환경 검증이 끝난 것으로 보지 않는다.**

| 항목 | 왜 미검증인가 | 확인 방법 |
|---|---|---|
| 실제 USB **분리** 감지 | 연결은 3-A에서 검증됨. 분리는 미검증 | 백업 중 USB 분리 |
| 앱 실행 **전부터** 꽂혀 있던 USB | 위와 같음 | USB를 꽂은 채로 앱 시작 |
| USB 외장 SSD (고정 디스크로 표시) | 해당 장치 없음. `MSFT_Disk.BusType==7` 판정 로직 자체가 미검증 | 외장 SSD 연결 후 백업 대상에 포함되는지 확인 |
| 드라이브 문자 변경 후 동일 장치 인식 | 볼륨 GUID 기반 로직이 실제 장치에서 미검증 | 디스크 관리에서 문자 변경 후 재연결 |
| WMI 판정 실패 시 동작 | 이 PC에서 WMI가 정상이라 실패 경로 미실행 | 문제 기록에 `BusTypeUnknown`이 남는지 확인 |
| **PowerPoint가 실제로 `~$` 파일을 만드는지** | 실제 PowerPoint로 USB의 PPTX를 열어보지 않았음. **등급 판정 전체가 여기 달려 있음** | USB의 .pptx를 PowerPoint로 열고, 상태 창에서 "영구·Drive"로 바뀌는지 확인 |
| 잠금 파일 이름 규칙의 실제 형태 | 문서화된 두 규칙으로 구현했으나 실제 PowerPoint 출력과 대조 안 함 | 열어 놓고 폴더에서 숨김 파일 표시 → 실제 이름 확인 |
| 복사 도중 USB 물리적 분리 | 취소 토큰으로 대체 검증했을 뿐, 실제 분리는 안 함 | 큰 파일 복사 중 USB 분리 |
| Windows 재부팅 후 자동 실행 | **사용자 PC를 임의로 재부팅하지 않음** | 자동 실행 등록 후 재부팅, 창 없이 1개 인스턴스만 뜨는지 확인 |
| 절전 복귀 후 재검사 | 실제 절전 미수행 | 절전 후 복귀 |
| 발표 중 성능 영향 | 실제 PowerPoint 발표·PDF 열기와 동시 실행 안 함 | 슬라이드쇼 중 백업을 돌리며 전환 지연 체감 측정 |
| 속도 제한 기본값(5/2 MiB/s) 적정성 | 실제 USB 속도로 측정 안 함 | 실제 장치에서 측정 후 설정 조정 |
| **PPTX/PDF가 PowerPoint·Acrobat에서 열리는지** | 자동 테스트는 ZIP/OPC 구조와 해시까지만 확인 | 복원한 파일을 실제 앱으로 열기 |
| **실제 Google 계정 연동** | 구현은 완료, 실계정 인증은 사용자만 가능 | 4-A 참고 |
| 원본 삭제·휴지통 비우기 | **사용자 실제 파일을 삭제하지 않음.** 테스트는 자체 생성 임시 파일만 삭제 | 폐기 가능한 테스트 파일로 확인 |
| 긴 경로(MAX_PATH 초과) | .NET이 `\\?\`를 자동 적용하므로 동작할 것으로 보이나 실제 깊은 USB 경로로 미확인 | 260자 넘는 경로에 파일을 두고 확인 |

---

## 3-A. 실제 장치 검증 (2026-09-09 12:00~12:23, 사용자 실행)

사용자가 배포 exe를 실제로 실행해 남은 기록. **이 부분은 모의가 아니라 실환경 결과다.**

확인된 것:

| 항목 | 결과 |
|---|---|
| 실제 USB 감지 | 성공. `Samsung UFS (E:)` 를 백업 대상으로 판정 |
| 대량 파일 스캔·복사 | **526개 신규 백업, 실패 0, 건너뜀 0** (약 23분) |
| 한글·공백·괄호가 섞인 실제 파일명 | 전부 정상 처리 |
| 깊은 중첩 경로 | 실제 6단계 이상 경로 정상 처리 |
| 대용량 파일 | 최대 208 MB PDF 정상 복사 |
| 등급 판정 | 전부 `Temporary` (구버전이라 MRU 없음 + 감시자 실패) |

발견된 문제:

- **감시자 시작 실패 (미해결·원인 미상).** `Could not start a watcher on E:\:` 로그가 남았고 예외 메시지가 비어 있어 원인을 특정할 수 없다.
  동일 조건에서 `FileSystemWatcher`를 E:\ 에 직접 걸어 보면 정상 시작하므로 상황 의존적이다.
  추측으로 고치지 않고 **예외 타입·HResult·InnerException을 남기도록** 로깅을 고쳤고, 실패해도 다음 스윕에서 재시도한다.
  주기 재탐색이 있으므로 이벤트를 놓쳐도 누락은 보완되지만, **실시간 잠금 파일 탐지는 이 경로가 살아야 동작한다.**
  다음 실행 때 같은 경고가 나오면 로그에 원인이 남는다.
- **임시 보관 누적량 → 조치 완료.** 526개 = **7.2 GB**, 그 중 PDF 511개 / PPTX 15개였다.
  사용자 결정에 따라 **PDF를 백업 대상에서 완전히 제외**했다(PDF는 잠금 파일도 최근 문서 기록도 없어
  애초에 영구 등급이 될 수 없다). 같은 USB라면 15개만 남는다.
  기존 7.2 GB는 사용자 요청으로 삭제했다 — 파일과 DB 행 526개를 함께 지워 상태를 일치시켰고,
  원본은 USB에 그대로 있으며 Drive에 올라간 것은 없었다(업로드 큐 0건). 장치 식별 행은 유지했다.

## 4. 미구현

- 단계 D의 설치 관리자. 현재는 단일 exe를 폴더에 두고 설정에서 자동 실행을 켜는 방식.
- 임시 보관량 상한. 위 3-A 참고.

## 4-A. 단계 C (Google Drive) — 구현 완료, 실계정 미검증

구현한 것: OAuth 데스크톱 흐름(`drive.file` 스코프), DPAPI 갱신 토큰 보관, 시작 시 무인 연결 복구,
전용 폴더 생성, `files.generateIds` 선발급, 재개 업로드(세션 URI·오프셋 DB 영속), 지수 백오프 + jitter +
`Retry-After` 존중, 조치 필요 상태 분리, Drive 다운로드 복원.

모의 Drive로 검증한 실패 경로 (`UploadWorkerTests`, 12개):

| 시나리오 | 확인한 내용 |
|---|---|
| 성공 응답 유실 | 재시도가 선발급 ID로 조회 → **중복 파일 생성 안 됨**, ID 발급 1회뿐 |
| 재개 세션 만료 | 새 세션으로 재시작, **파일 1개만 존재**, ID 재사용 |
| 일시적 5xx | 백오프 후 재시도 예약, 즉시 재시도 안 함 |
| Drive 용량 부족 | `NeedsAttention` + 재시도 예약 없음, 로컬 사본 보존 |
| 권한 철회 | 업로드만 중단, 로컬 백업 보존, 재연결 시 큐 복귀 |
| 다른 계정으로 재연결 | 대기분을 조용히 올리지 않고 `NeedsAttention` |
| 수정 후 재업로드 | 이전 버전 덮어쓰지 않고 **독립 파일 2개** |
| 로컬 사본 유실 | 무한 재시도 대신 조치 필요 |
| Drive 확인 후 로컬 해제 → 복원 | Drive에서 내려받아 **SHA-256 일치** 확인 |
| 미연결 상태 복원 시도 | 실패 사유 표시 |

**미검증 (실계정 필요):** 실제 OAuth 로그인·동의, 실제 Drive 업로드/다운로드, 토큰 7일 만료 동작,
Workspace 관리자 정책, 실제 네트워크에서의 재개 업로드.

## 5. 알려진 한계 (설계상 의도된 것)

- 앱이 꺼져 있는 동안 열었다 닫은 발표자료는 임시 등급에 남는다. 나중에 열리면 승격된다.
- PDF는 소유자 파일이 없어 영구 등급이 될 수 없다.
- 영구 등급 파일도 업로드 완료 + 7일 후에는 로컬에서 사라지므로, 그 시점부터 복원이 Drive 단독 의존이 된다.
- 주기 재탐색은 크기·수정 시각만 비교한다. 내용이 바뀌었는데 둘 다 같은 경우는 놓친다
  (새 연결·변경 이벤트 때는 실제 내용을 읽으므로 그때 잡힌다).
