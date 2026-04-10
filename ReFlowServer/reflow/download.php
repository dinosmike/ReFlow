<?php
declare(strict_types=1);

// Streams bytes for download speed testing.
// Query:
// - seconds: stream for N seconds (preferred)
// - bytes: stream exactly N bytes
// - debug: returns diagnostic text instead of streaming

if (isset($_GET['debug'])) {
  ini_set('display_errors', '1');
  ini_set('display_startup_errors', '1');
  error_reporting(E_ALL);
  header('Content-Type: text/plain; charset=utf-8');
  echo "download debug\n";
  echo "version=" . PHP_VERSION . "\n";
  echo "ob_level=" . ob_get_level() . "\n";
  exit;
}

header('Content-Type: application/octet-stream');
header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');
header('Pragma: no-cache');
header('Expires: 0');
header('X-Accel-Buffering: no'); // nginx: disable buffering if enabled

@ini_set('output_buffering', '0');
@ini_set('zlib.output_compression', '0');
@ini_set('implicit_flush', '1');
while (ob_get_level() > 0) { @ob_end_clean(); }
@ob_implicit_flush(true);

$seconds = isset($_GET['seconds']) ? (int)$_GET['seconds'] : 0;
$bytes = isset($_GET['bytes']) ? (int)$_GET['bytes'] : 0;

if ($seconds <= 0 && $bytes <= 0) {
  $seconds = 15;
}

$chunkSize = 1024 * 256; // 256KB
$chunk = str_repeat("\0", $chunkSize);

$start = microtime(true);
$sent = 0;

if ($bytes > 0) {
  // exact-size mode
  while ($sent < $bytes) {
    $remain = $bytes - $sent;
    $len = $remain >= $chunkSize ? $chunkSize : $remain;
    echo ($len === $chunkSize) ? $chunk : substr($chunk, 0, $len);
    $sent += $len;
    flush();
    if (connection_aborted()) break;
  }
  exit;
}

// timed mode
while (true) {
  $now = microtime(true);
  if (($now - $start) >= $seconds) break;
  echo $chunk;
  $sent += $chunkSize;
  flush();
  if (connection_aborted()) break;
}

exit;

