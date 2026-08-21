<?php

namespace PenguinPanel\WhmcsAddon;

class ApiException extends \RuntimeException
{
    private $statusCode;

    public function __construct($message, $statusCode = 0)
    {
        parent::__construct($message, (int) $statusCode);
        $this->statusCode = (int) $statusCode;
    }

    public function getStatusCode()
    {
        return $this->statusCode;
    }
}

class ApiClient
{
    private $baseUrl;
    private $apiKey;

    public function __construct($baseUrl, $apiKey)
    {
        $this->baseUrl = rtrim(trim((string) $baseUrl), '/');
        $this->apiKey = trim((string) $apiKey);

        if ($this->baseUrl === '') {
            throw new \InvalidArgumentException('PenguinPanel URL is empty. Configure the addon first.');
        }
        if ($this->apiKey === '') {
            throw new \InvalidArgumentException('PenguinPanel API key is empty. Configure the addon first.');
        }
    }

    public function get($path, $timeout = 30)
    {
        if (!function_exists('curl_init')) {
            throw new ApiException('PHP cURL is required by the PenguinPanel addon.');
        }

        $url = $this->baseUrl . '/' . ltrim((string) $path, '/');
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_HTTPGET => true,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT => max(15, (int) $timeout),
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_MAXREDIRS => 0,
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_SSL_VERIFYHOST => 2,
            CURLOPT_USERAGENT => 'PenguinPanel-WHMCS-Addon/1.1',
            CURLOPT_HTTPHEADER => [
                'Accept: application/json',
                'X-Penguin-WHMCS-Key: ' . $this->apiKey,
            ],
        ]);

        $response = curl_exec($ch);
        $error = curl_error($ch);
        $errno = curl_errno($ch);
        $status = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);

        if ($response === false) {
            throw new ApiException('Could not connect to PenguinPanel: cURL ' . $errno . ' - ' . $error);
        }

        $raw = trim((string) $response);
        $decoded = $raw === '' ? [] : json_decode($raw, true);

        if ($status < 200 || $status >= 300) {
            $message = '';
            if (is_array($decoded)) {
                foreach (['error', 'detail', 'message', 'title'] as $field) {
                    if (!empty($decoded[$field]) && is_scalar($decoded[$field])) {
                        $message = (string) $decoded[$field];
                        break;
                    }
                }
            }
            if ($message === '') {
                $message = $raw !== '' ? preg_replace('/\s+/', ' ', strip_tags($raw)) : 'HTTP ' . $status;
            }
            throw new ApiException('PenguinPanel API: ' . $message, $status);
        }

        if (!is_array($decoded)) {
            throw new ApiException('PenguinPanel returned HTTP ' . $status . ' but the response was not valid JSON.', $status);
        }

        return $decoded;
    }
}
