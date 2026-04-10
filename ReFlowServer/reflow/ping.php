<?php
declare(strict_types=1);

// Minimal low-overhead endpoint for connectivity checks.
// Debug: add ?debug=1 to see PHP version (returns 200 text/plain).
if (isset($_GET['debug'])) {
  ini_set('display_errors', '1');
  ini_set('display_startup_errors', '1');
  error_reporting(E_ALL);
  header('Content-Type: text/plain; charset=utf-8');
  echo "ping ok\n";
  echo "version=" . PHP_VERSION . "\n";
  exit;
}

header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');
header('Pragma: no-cache');
http_response_code(204);
exit;

