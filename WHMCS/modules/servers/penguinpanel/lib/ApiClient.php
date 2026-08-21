<?php

class PenguinPanelApiException extends RuntimeException
{
    private $statusCode;
    private $responseData;

    public function __construct($message, $statusCode = 0, $responseData = null)
    {
        parent::__construct($message, (int) $statusCode);
        $this->statusCode = (int) $statusCode;
        $this->responseData = $responseData;
    }

    public function getStatusCode()
    {
        return $this->statusCode;
    }

    public function getResponseData()
    {
        return $this->responseData;
    }
}

class PenguinPanelApiClient
{
    private $baseUrl;
    private $apiKey;

    public function __construct($baseUrl, $apiKey)
    {
        $this->baseUrl = rtrim((string) $baseUrl, '/');
        $this->apiKey = trim((string) $apiKey);

        if ($this->baseUrl === '') {
            throw new InvalidArgumentException('PenguinPanel API URL is empty.');
        }
        if ($this->apiKey === '') {
            throw new InvalidArgumentException('PenguinPanel API key is empty.');
        }
    }

    public function get($path, $timeout = 60)
    {
        return $this->request('GET', $path, null, $timeout);
    }

    public function post($path, $body = null, $timeout = 120)
    {
        return $this->request('POST', $path, $body, $timeout);
    }

    public function delete($path, $timeout = 600)
    {
        return $this->request('DELETE', $path, null, $timeout);
    }

    public function request($method, $path, $body = null, $timeout = 60)
    {
        if (!function_exists('curl_init')) {
            throw new PenguinPanelApiException('PHP cURL is required by the PenguinPanel WHMCS module.');
        }

        $url = $this->baseUrl . '/' . ltrim((string) $path, '/');
        $headers = [
            'Accept: application/json',
            'X-Penguin-WHMCS-Key: ' . $this->apiKey,
        ];

        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CUSTOMREQUEST => strtoupper((string) $method),
            CURLOPT_CONNECTTIMEOUT => 15,
            CURLOPT_TIMEOUT => max(15, (int) $timeout),
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_MAXREDIRS => 0,
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_SSL_VERIFYHOST => 2,
            CURLOPT_USERAGENT => 'PenguinPanel-WHMCS/1.0',
            CURLOPT_HTTPHEADER => $headers,
        ]);

        if ($body !== null) {
            $json = json_encode($body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
            if ($json === false) {
                curl_close($ch);
                throw new PenguinPanelApiException('Could not encode the PenguinPanel API request as JSON.');
            }
            $headers[] = 'Content-Type: application/json';
            curl_setopt($ch, CURLOPT_HTTPHEADER, $headers);
            curl_setopt($ch, CURLOPT_POSTFIELDS, $json);
        }

        $response = curl_exec($ch);
        $curlError = curl_error($ch);
        $curlErrno = curl_errno($ch);
        $statusCode = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);

        if ($response === false) {
            throw new PenguinPanelApiException(
                'Could not connect to PenguinPanel: cURL ' . $curlErrno . ' - ' . $curlError,
                0
            );
        }

        $decoded = null;
        $trimmed = trim((string) $response);
        if ($trimmed !== '') {
            $decoded = json_decode($trimmed, true);
        }

        if ($statusCode < 200 || $statusCode >= 300) {
            $message = $this->extractError($decoded, $trimmed, $statusCode);
            throw new PenguinPanelApiException($message, $statusCode, $decoded);
        }

        if ($trimmed === '') {
            return [];
        }

        if (!is_array($decoded)) {
            throw new PenguinPanelApiException(
                'PenguinPanel returned HTTP ' . $statusCode . ' but the response was not valid JSON.',
                $statusCode
            );
        }

        return $decoded;
    }

    private function extractError($decoded, $raw, $statusCode)
    {
        if (is_array($decoded)) {
            foreach (['error', 'detail', 'message', 'title'] as $field) {
                if (!empty($decoded[$field]) && is_scalar($decoded[$field])) {
                    return 'PenguinPanel API: ' . (string) $decoded[$field];
                }
            }
        }

        if ($raw !== '') {
            $safe = preg_replace('/\s+/', ' ', strip_tags($raw));
            if (strlen($safe) > 300) {
                $safe = substr($safe, 0, 300) . '...';
            }
            return 'PenguinPanel API returned HTTP ' . $statusCode . ': ' . $safe;
        }

        return 'PenguinPanel API returned HTTP ' . $statusCode . '.';
    }
}
